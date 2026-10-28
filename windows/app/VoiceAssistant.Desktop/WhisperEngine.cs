using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed class WhisperEngine : IDisposable
{
    public static WhisperEngine Shared { get; } = new(WhisperAssets.Home);
    readonly string root;
    readonly Func<WhisperPrerequisiteStatus> prerequisites;
    readonly SemaphoreSlim serial = new(1, 1);
    readonly object sync = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Timer idle;
    Process? process;
    WhisperProcessJob? job;
    Uri? endpoint;
    string mode = "";
    DateTime used;
    bool gpu, disposed;
    public WhisperEngine(string directory, Func<WhisperPrerequisiteStatus>? checkPrerequisites = null)
    {
        root = Path.GetFullPath(directory);
        prerequisites = checkPrerequisites ?? WhisperPrerequisites.CheckSystem;
        idle = new Timer(_ => TrimIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
    public static bool Enabled(UiSettings settings) => settings.WhisperMode is "cpu" or "cuda" || settings.WhisperMode == "external" && !string.IsNullOrWhiteSpace(settings.WhisperUrl);
    public int ProcessId { get { lock (sync) return process is { HasExited: false } ? process.Id : 0; } }
    public void KeepWarm() { lock (sync) if (process is { HasExited: false }) used = DateTime.UtcNow; }
    public string Status { get { lock (sync) return process is { HasExited: false } && endpoint != null ? gpu ? "NVIDIA 가속 준비됨" : "CPU 보정 준비됨" : "보정 엔진 대기 중"; } }
    public async Task InstallAsync(string selectedMode, IProgress<ModelProgress> progress, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await serial.WaitAsync(linked.Token);
        try { Stop(); RequirePrerequisites(); await WhisperAssets.InstallAsync(root, selectedMode, progress, linked.Token).ConfigureAwait(false); }
        finally { serial.Release(); }
    }
    public async Task<string> CheckAsync(string selectedMode, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await serial.WaitAsync(linked.Token);
        try { await StartAsync(selectedMode, linked.Token).ConfigureAwait(false); return Status; }
        finally { serial.Release(); }
    }
    public async Task<WhisperTranscript> RefineAsync(UiSettings settings, string wav, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await serial.WaitAsync(linked.Token);
        try { return await RefineCoreAsync(settings, wav, linked.Token).ConfigureAwait(false); }
        finally { serial.Release(); }
    }
    public async Task<WhisperTranscript?> TryRefineAsync(UiSettings settings, string wav, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        // Idle name checks never queue behind a command, installation or another check.
        if (!await serial.WaitAsync(0, linked.Token)) return null;
        try { return await RefineCoreAsync(settings, wav, linked.Token).ConfigureAwait(false); }
        finally { serial.Release(); }
    }
    async Task<WhisperTranscript> RefineCoreAsync(UiSettings settings, string wav, CancellationToken token)
    {
        _ = ConversationLanguage.Get(settings.Language);
        if (settings.WhisperMode == "external") return await SpeechInput.RefineAsync(settings.WhisperUrl, wav, token, settings.Text.CustomWords, settings.Language);
        if (!Enabled(settings)) return WhisperTranscript.Empty;
        try
        {
            await StartAsync(settings.WhisperMode, token).ConfigureAwait(false);
            Process own; Uri route;
            lock (sync) { own = process ?? throw new IOException("음성 보정 엔진이 종료됐어요."); route = endpoint!; }
            // An unrelated loopback service must never receive captured speech.
            if (!WhisperProcessJob.OwnsListener(own.Id, route.Port)) throw new IOException("음성 보정 엔진의 연결을 확인하지 못했어요.");
            using var cancel = token.Register(() => StopOwned(own));
            var answer = await SpeechInput.RefineEndpointAsync(route, wav, token, settings.Text.CustomWords, settings.Language).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); lock (sync) used = DateTime.UtcNow;
            return answer;
        }
        catch { Stop(); throw; }
    }
    async Task StartAsync(string selectedMode, CancellationToken token)
    {
        _ = WhisperAssets.Archive(selectedMode);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mode == selectedMode && process is { HasExited: false } && endpoint != null && WhisperProcessJob.OwnsListener(process.Id, endpoint.Port)) { used = DateTime.UtcNow; return; }
        }
        Stop();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        try
        {
            RequirePrerequisites();
            await WhisperAssets.VerifyAsync(root, selectedMode, ct).ConfigureAwait(false);
            WhisperPrerequisites.RequireImports(WhisperAssets.Runtime(root, selectedMode));
            var publicPath = Path.Combine(root, "empty-public"); WhisperAssets.PlainDirectory(publicPath); Directory.CreateDirectory(publicPath);
            if (Directory.EnumerateFileSystemEntries(publicPath).GetEnumerator() is { } items) { using (items) if (items.MoveNext()) throw new IOException("보정 엔진의 빈 공개 폴더를 확인해 주세요."); }
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var prefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var baseUri = new Uri($"http://127.0.0.1:{port}{prefix}/");
            var runtime = WhisperAssets.Runtime(root, selectedMode);
            var info = new ProcessStartInfo(Path.Combine(runtime, "whisper-server.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtime,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            foreach (var arg in new[] { "-m", WhisperAssets.ModelPath(root), "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--request-path", prefix, "--public", publicPath, "-l", "ko", "-t", "4", "-sns", "-fa" }) info.ArgumentList.Add(arg);
            if (selectedMode == "cpu") info.ArgumentList.Add("-ng");
            Process own; int sawGpu = 0;
            lock (sync)
            {
                ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
                own = new Process { StartInfo = info }; process = own; mode = selectedMode; gpu = false; endpoint = null;
                // Drain native output without persisting audio text, paths or the private request prefix.
                own.OutputDataReceived += (_, _) => { };
                own.ErrorDataReceived += (_, e) => { if (e.Data?.StartsWith("whisper_backend_init_gpu: using CUDA", StringComparison.Ordinal) == true) Interlocked.Exchange(ref sawGpu, 1); };
                try { own.Start(); job = new WhisperProcessJob(own); own.StandardInput.Close(); own.BeginOutputReadLine(); own.BeginErrorReadLine(); }
                catch { StopLocked(); throw; }
            }
            using var cancel = ct.Register(() => StopOwned(own));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (sync) if (process != own || own.HasExited) throw new IOException("음성 보정 엔진을 시작하지 못했어요. 설치·복구를 실행해 주세요.");
                if (WhisperProcessJob.OwnsListener(own.Id, port))
                {
                    try
                    {
                        using var response = await client.GetAsync(new Uri(baseUri, "health"), ct).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                            if (doc.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok") break;
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { ct.ThrowIfCancellationRequested(); }
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            lock (sync)
            {
                ct.ThrowIfCancellationRequested();
                if (process != own || own.HasExited) throw new IOException("보정 엔진이 종료됐어요.");
                gpu = Volatile.Read(ref sawGpu) != 0;
                if (selectedMode == "cuda" && !gpu) throw new IOException("NVIDIA 가속을 사용할 수 없어요. 드라이버를 확인하거나 CPU 방식을 선택해 주세요.");
                endpoint = new Uri(baseUri, "inference"); used = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { Stop(); throw new TimeoutException("보정 엔진 준비 시간이 초과됐어요. 기본 인식으로 계속합니다."); }
        catch { Stop(); throw; }
    }
    void TrimIdle()
    {
        if (!serial.Wait(0)) return;
        try { lock (sync) if (process != null && DateTime.UtcNow - used > TimeSpan.FromMinutes(3)) StopLocked(); }
        finally { serial.Release(); }
    }
    void RequirePrerequisites()
    {
        var status = prerequisites(); if (!status.Ready) throw new WhisperPrerequisiteException(status.Detail);
    }
    public void Stop() { lock (sync) StopLocked(); }
    void StopOwned(Process own) { lock (sync) if (process == own) StopLocked(); }
    void StopLocked()
    {
        var own = process; process = null; endpoint = null; gpu = false;
        if (own != null)
        {
            try { if (!own.HasExited) { own.Kill(true); own.WaitForExit(2000); } } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            finally { own.Dispose(); }
        }
        job?.Dispose(); job = null;
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        lifetime.Cancel(); idle.Dispose(); Stop();
    }
}
