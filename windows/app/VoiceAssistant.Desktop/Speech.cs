using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed class SpeechInput : IDisposable
{
    Process? process;
    int captureProcessId;
    public event Action<int, JsonElement>? Message;
    public event Action<int, string>? Error;
    public int Generation { get; private set; }
    public int CaptureRequest { get; private set; }
    public bool Running => process is { HasExited: false };
    public int ProcessId
    {
        get { try { return Running ? (captureProcessId > 0 ? captureProcessId : process!.Id) : 0; } catch (InvalidOperationException) { return 0; } }
    }
    public void ObserveReady(JsonElement message)
    {
        // Called on the UI thread after its generation check, just like Start and Stop.
        // Windows venv launchers use a child Python process for the actual capture stream.
        if (message.TryGetProperty("process_id", out var pid) && pid.TryGetInt32(out var actual) && actual > 0) captureProcessId = actual;
    }
    public static ProcessStartInfo PythonInfo(params string[] args)
    {
        if (!File.Exists(AppPaths.Python)) throw new InvalidOperationException("음성 실행 환경이 없어요. Windows 설치 프로그램을 다시 실행하거나 소스 폴더의 windows\\setup.ps1을 실행하세요.");
        var info = new ProcessStartInfo(AppPaths.Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = AppPaths.WindowsRoot };
        info.Environment["PYTHONUTF8"] = "1"; info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["VOICE_ASSISTANT_MODEL"] = AppPaths.Model;
        info.ArgumentList.Add("-B"); info.ArgumentList.Add("-u"); info.ArgumentList.Add(AppPaths.Worker);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public void Start(UiSettings settings)
    {
        MultilingualSpeech.ValidateInput(settings);
        if (Running) return;
        var p = new Process { StartInfo = PythonInfo(), EnableRaisingEvents = true }; process = p; captureProcessId = 0; CaptureRequest = 0;
        var generation = ++Generation;
        var error = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || process != p) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                Message?.Invoke(generation, doc.RootElement.Clone());
            }
            catch (JsonException) { }
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null && error.Length < 4000) error.AppendLine(e.Data); };
        p.Exited += (_, _) => { if (process == p) { process = null; Error?.Invoke(generation, p.ExitCode == 0 ? "음성 입력이 종료됐어요." : "음성 입력을 시작하지 못했어요. " + error.ToString()); } };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        var configuration = JsonSerializer.SerializeToNode(settings)!.AsObject();
        configuration["InitialCapture"] = false; // Wait for the UI's current microphone gate.
        p.StandardInput.WriteLine(configuration.ToJsonString()); p.StandardInput.Flush();
    }
    public void Capture(bool enabled) => Send(new { type = "capture", enabled, request = ++CaptureRequest });
    public void EndUtterance() => Send(new { type = "finish" });
    public void BeginBarge() => Send(new { type = "barge" });
    void Send(object value) { try { if (Running) { process!.StandardInput.WriteLine(JsonSerializer.Serialize(value)); process.StandardInput.Flush(); } } catch (InvalidOperationException) { } catch (IOException) { } }
    public void Stop()
    {
        Generation++;var p = process; process = null; captureProcessId = 0; if (p == null) return;
        try { if (!p.HasExited) { p.StandardInput.WriteLine("{\"type\":\"shutdown\"}"); p.StandardInput.Close(); if (!p.WaitForExit(1200)) p.Kill(true); } } catch { } finally { p.Dispose(); }
    }
    public void Dispose() => Stop();
    public static async Task<IReadOnlyList<DeviceOption>> DevicesAsync()
    {
        using var p = new Process { StartInfo = PythonInfo("--devices") }; p.Start(); p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(timeout.Token); } catch { if (!p.HasExited) p.Kill(true); throw; }
        var text = await stdout; var error = await stderr;
        if (p.ExitCode != 0) throw new InvalidOperationException(error);
        foreach (var line in text.Split('\n').Where(x => x.StartsWith('{'))) { using var d = JsonDocument.Parse(line); if (d.RootElement.GetProperty("type").GetString() == "devices") return JsonSerializer.Deserialize<List<DeviceOption>>(d.RootElement.GetProperty("devices")) ?? new(); }
        return new[] { new DeviceOption("", "Windows 기본 마이크") };
    }
    internal static async Task<WhisperTranscript> RefineAsync(string url, string wav, CancellationToken ct, string[]? words = null, string language = "ko")
    {
        var u = SettingsStore.ValidateLocalUrl(url);
        var endpoint = new Uri(u.GetLeftPart(UriPartial.Authority) + "/inference");
        return await RefineEndpointAsync(endpoint, wav, ct, words, language);
    }
    internal static async Task<WhisperTranscript> RefineEndpointAsync(Uri endpoint, string wav, CancellationToken ct, string[]? words = null, string language = "ko")
    {
        SettingsStore.ValidateLocalUrl(endpoint.AbsoluteUri);
        _ = ConversationLanguage.Get(language);
        if (wav.Length > 3_000_000) throw new ArgumentException("보정할 음성이 너무 길어요.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 1_048_576 };
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(Convert.FromBase64String(wav)); content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(content, "file", "speech.wav"); form.Add(new StringContent(language), "language"); form.Add(new StringContent("verbose_json"), "response_format");
        // Confidence is available independently of token timing; timing is costly on CPU and unused here.
        form.Add(new StringContent("false"), "token_timestamps");
        // The request supplies the language; an extra language-detection pass is unnecessary.
        form.Add(new StringContent("true"), "no_language_probabilities");
        form.Add(new StringContent("0"), "temperature"); form.Add(new StringContent("0"), "temperature_inc");
        if (words is { Length: > 0 })
        {
            new TextPreferences { CustomWords = words }.Validate();
            form.Add(new StringContent(string.Join(", ", words)), "prompt");
        }
        try
        {
            using var res = await client.PostAsync(endpoint, form, ct); res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)); return WhisperTranscript.Parse(doc.RootElement);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("음성 보정 응답 시간이 초과됐어요."); }
    }
}

public sealed class SpeechOutput : IDisposable
{
    readonly object gate = new();
    CancellationTokenSource? cancel;
    public static IReadOnlyList<DeviceOption> Options(bool outputs)
    {
        var result=new List<DeviceOption>{new("",outputs?"Windows 기본 출력 장치":"한국어 음성 자동 선택")};
        object? instance=null;object? collection=null;
        try
        {
            var type=Type.GetTypeFromProgID("SAPI.SpVoice");if(type==null)return result;
            instance=Activator.CreateInstance(type)!;dynamic voice=instance;
            collection=outputs?voice.GetAudioOutputs():voice.GetVoices();dynamic tokens=collection;
            for(int i=0;i<tokens.Count;i++)
            {
                object item=tokens.Item(i);try{dynamic token=item;result.Add(new((string)token.Id,(string)token.GetDescription()));}finally{Marshal.FinalReleaseComObject(item);}
            }
        }
        catch(Exception ex){AppPaths.Log("tts-options",ex.Message);}
        finally{if(collection!=null)Marshal.FinalReleaseComObject(collection);if(instance!=null)Marshal.FinalReleaseComObject(instance);}
        return result;
    }
    public static string[] Voices()=>Options(false).Skip(1).Select(x=>x.Label).ToArray();
    public async Task<SpeechPlaybackResult> SpeakAsync(string text, CancellationToken token,UiSettings? settings=null, VoicePlaybackObserver? observer=null)
    {
        token.ThrowIfCancellationRequested();
        var options=settings??new UiSettings();SettingsStore.Validate(options);
        CancellationTokenSource own;
        lock (gate) { cancel?.Cancel(); own = CancellationTokenSource.CreateLinkedTokenSource(token); cancel = own; }
        try { return await SpeechPipeline.RunAsync(text.Length > 1500 ? text[..1500] : text, options, own.Token,
            (value, prefs, ct) => ExternalVoice.FetchAsync(value, prefs, ct, language: options.Language),
            (audio, prefs, ct) => VoicePlayback.PlayAsync(audio, prefs, ct, observer),
            async (value, prefs, ct) => await VoicePlayback.PlayAsync(await SapiSpeech.RenderAsync(value, prefs, ct), prefs, ct, observer)); }
        finally { lock (gate) { if (ReferenceEquals(cancel, own)) cancel = null; } own.Dispose(); }
    }
    public void Stop() { lock (gate) { cancel?.Cancel(); } }
    public void Dispose() => Stop();
}
