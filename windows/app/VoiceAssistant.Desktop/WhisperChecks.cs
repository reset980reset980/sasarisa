using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class WhisperChecks
{
    internal static async Task<int> RunJobHostAsync()
    {
        if (Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var source = Environment.GetEnvironmentVariable("SASARISA_WHISPER_CHECK_ROOT"); if (source == null) return 2;
        using var engine = new WhisperEngine(source); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await engine.CheckAsync("cuda", timeout.Token);
        File.WriteAllText(Path.Combine(AppPaths.Home, "whisper-child.pid"), engine.ProcessId.ToString());
        await Task.Delay(TimeSpan.FromSeconds(45), timeout.Token); return 0;
    }
    sealed class DownloadHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    sealed class QuietProgress : IProgress<ModelProgress> { public void Report(ModelProgress value) { } }
    static ModelFile FileRecord(string name, byte[] bytes) => new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task<int> RunAsync(List<object> rows, bool native = false)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var root = Path.Combine(AppPaths.Home, "whisper-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var progress = new QuietProgress();
        await Check("Whisper legacy settings preserve external URL and managed preference round trips", () =>
        {
            var legacy = System.Text.Json.JsonSerializer.Deserialize<UiSettings>("{\"WhisperUrl\":\"http://127.0.0.1:1234\"}")!;
            Require(legacy.WhisperMode == "external" && WhisperEngine.Enabled(legacy), "Legacy URL disabled");
            Require(!WhisperEngine.Enabled(new()), "Default starts engine");
            var current = legacy with { WhisperMode = "cuda" }; var imported = legacy with { WhisperMode = "cpu" };
            Require(SettingsTransfer.Parse(SettingsTransfer.Serialize(current)).WhisperMode == "cuda", "Mode lost in export");
            Require(SettingsTransfer.Prepare(imported, current, false, false).WhisperMode == "cuda", "Import changed unselected connection");
            try { SettingsStore.Validate(legacy with { WhisperMode = "command" }); throw new Exception("Unknown mode accepted"); } catch (ArgumentException) { }
            return Task.CompletedTask;
        });
        await Check("Whisper owns only the correct loopback listener PID and port", () =>
        {
            var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            try { int port = ((IPEndPoint)server.LocalEndpoint).Port; Require(WhisperProcessJob.OwnsListener(Environment.ProcessId, port), "Own listener missed"); Require(!WhisperProcessJob.OwnsListener(int.MaxValue, port), "Foreign listener accepted"); }
            finally { server.Stop(); }
            return Task.CompletedTask;
        });
        await Check("Whisper keeps quick commands immediate and preserves related dictation corrections", () =>
        {
            Require(WhisperText.Quick(CommandParser.Parse("지금 몇 시야")), "Clock waits for refinement");
            foreach (var clock in new[] { "지금 몇시냐?", "몇 시니", "몇 시예요", "지금 몇 시인가요" }) Require(WhisperText.Quick(CommandParser.Parse(clock)), "Natural clock phrase lost: " + clock);
            Require(!WhisperText.Quick(CommandParser.Parse("블렌다 30분 타이머")), "Free text bypasses refinement");
            Require(WhisperText.Select("블렌다에서 간판을 만들어 줘", "블렌더에서 간판을 만들어 줘.") == "블렌더에서 간판을 만들어 줘.", "Useful correction rejected");
            Require(WhisperText.Select("구독과 좋아요라고 써줘", "구독과 좋아요라고 써줘") == "구독과 좋아요라고 써줘", "User dictation censored");
            return Task.CompletedTask;
        });
        await Check("Whisper rejects unrelated text noise hallucinations and different-language substitutions", () =>
        {
            const string live = "블렌더를 열어 줘";
            foreach (var other in new[] { "Thanks for watching", "시청해 주셔서 감사합니다.", "I like coffee and the weather.", "전국의 감염병 통계를 자세하게 설명해드리겠습니다", "- 메모장을 닫아 - 계산기를 켜" }) Require(WhisperText.Select(live, other) == live, "Unrelated text accepted: " + other);
            Require(WhisperText.Select(live, "블렌더를 열어 줘 고팩 고팩 고팩") == live, "Repeated noise kept");
            return Task.CompletedTask;
        });
        await Check("Whisper verbose tokens join Korean pieces and keep the least certain piece", () =>
        {
            using var json = JsonDocument.Parse("""
                {"text":"  블렌더 삼십 분 타이머  ","segments":[{"words":[
                {"word":" 블","probability":0.9},{"word":"렌","probability":0.2},{"word":"더","probability":0.8},
                {"word":" 삼","probability":0.7},{"word":"십","probability":0.6}]},
                {"words":[{"word":" 분","probability":0.9},{"word":" 타이머","probability":1}]}]}
                """);
            var transcript = WhisperTranscript.Parse(json.RootElement);
            Require(transcript.Text == "블렌더 삼십 분 타이머" && transcript.Words.Count == 4, "Text or word boundaries lost");
            Require(transcript.Words[0] == new WhisperWord("블렌더", 0.2) && transcript.Words[1] == new WhisperWord("삼십", 0.6), "Uncertain suffix ignored");
            using var legacy = JsonDocument.Parse("{\"text\":\"문장\"}");
            Require(WhisperTranscript.Parse(legacy.RootElement).Words.Count == 0, "Text-only reply invented certainty");
            return Task.CompletedTask;
        });
        await Check("Whisper malformed confidence and oversized transcripts cannot override live speech", () =>
        {
            foreach (var probability in new[] { "-0.1", "1.1", "1e400", "null", "\"0.2\"" })
            {
                using var json = JsonDocument.Parse("{\"text\":\"삼십 분 타이머\",\"segments\":[{\"words\":[{\"word\":\" 삼십\",\"probability\":" + probability + "}]}]}");
                try { WhisperTranscript.Parse(json.RootElement); throw new Exception("Malformed confidence accepted: " + probability); } catch (JsonException) { }
            }
            foreach (var raw in new[] { "{}", "{\"text\":12}", "{\"text\":\"말\",\"segments\":{}}", "{\"text\":\"말\",\"segments\":[null]}", "{\"text\":\"말\",\"segments\":[{\"words\":[{\"word\":\"말\"}]}]}", JsonSerializer.Serialize(new { text = new string('가', 6001) }) })
            {
                using var json = JsonDocument.Parse(raw);
                try { WhisperTranscript.Parse(json.RootElement); throw new Exception("Invalid transcript accepted"); } catch (JsonException) { }
            }
            return Task.CompletedTask;
        });
        await Check("Whisper uncertain novel words fall back while supported words and boundary confidence survive", () =>
        {
            const string live = "블렌다 삼십 분 타이머"; const string corrected = "블렌더 삼십 분 타이머";
            Require(WhisperText.Select(live, corrected, new[] { new WhisperWord("블렌더", 0.34) }) == live, "Uncertain correction accepted");
            Require(WhisperText.Select(live, corrected, new[] { new WhisperWord("블렌더", 0.35) }) == corrected, "Confidence boundary changed");
            Require(WhisperText.Select(live, corrected, new[] { new WhisperWord("삼십", 0.01) }) == corrected, "Corroborated word rejected");
            Require(WhisperText.Select("chrome 삼십 분 타이머", "크롬 삼십 분 타이머", new[] { new WhisperWord("크롬", 0.01) }) == "크롬 삼십 분 타이머", "Latin/Hangul corroboration lost");
            Require(WhisperText.Select("삼십 분", "삼십 분 타이머 시작해", new[] { new WhisperWord("시작해", 0.01) }) == "삼십 분 타이머 시작해", "Short live fragment vetoed longer related speech");
            return Task.CompletedTask;
        });
        await Check("Whisper discarded noise and removed wake words do not veto retained corrections", () =>
        {
            const string live = "블렌다 삼십 분 타이머"; const string corrected = "블렌더 삼십 분 타이머";
            var words = new[] { new WhisperWord("고팩", 0.01), new WhisperWord("자비스", 0.01) };
            Require(WhisperText.Select(live, corrected + " 고팩 고팩 고팩", words) == corrected, "Dropped noise vetoed correction");
            Require(WhisperText.Select(live, corrected, words) == corrected, "Stripped wake name vetoed command");
            Require(WhisperText.Select(live, corrected + ". " + corrected + ".", words) == corrected + ".", "Duplicate sentence not collapsed before length check");
            return Task.CompletedTask;
        });
        await Check("Whisper download rejects size mismatch and digest mismatch before use", async () =>
        {
            var bytes = Encoding.UTF8.GetBytes("fixture engine"); var file = FileRecord("engine.zip", bytes);
            using var client = new HttpClient(new DownloadHandler(bytes)); var target = Path.Combine(root, "download.zip");
            await WhisperAssets.DownloadAsync(client, new Uri("https://example.test/engine.zip"), target, file, progress, default);
            Require(await ModelDownload.MatchesAsync(target, file, default), "Valid download rejected");
            foreach (var bad in new[] { file with { Length = 2 }, file with { Sha256 = new string('0', 64) } })
            {
                try { await WhisperAssets.DownloadAsync(client, new Uri("https://example.test/engine.zip"), Path.Combine(root, Guid.NewGuid() + ".zip"), bad, progress, default); throw new Exception("Bad download accepted"); } catch (IOException) { }
            }
        });
        await Check("Whisper archive traversal duplicates corruption and cancellation preserve old runtime", async () =>
        {
            var bytes = Encoding.UTF8.GetBytes("verified runtime"); var file = FileRecord("whisper-server.exe", bytes); var destination = Path.Combine(root, "runtime"); Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "old"), "old runtime");
            string Zip(string name, string[] entries, byte[] content)
            {
                var path = Path.Combine(root, name); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                foreach (var entry in entries) { using var output = zip.CreateEntry(entry).Open(); output.Write(content); } return path;
            }
            foreach (var zip in new[] { Zip("traversal.zip", new[] { "Release/../escape", "Release/whisper-server.exe" }, bytes), Zip("duplicate.zip", new[] { "Release/whisper-server.exe", "Release/WHISPER-SERVER.EXE" }, bytes), Zip("corrupt.zip", new[] { "Release/whisper-server.exe" }, Encoding.UTF8.GetBytes("corrupt runtime!")) })
            {
                try { await WhisperAssets.ExtractAsync(zip, destination, new[] { file }, progress, default); throw new Exception("Bad archive accepted"); } catch (IOException) { }
                Require(File.ReadAllText(Path.Combine(destination, "old")) == "old runtime", "Old runtime changed");
            }
            var valid = Zip("valid.zip", new[] { "Release/whisper-server.exe" }, bytes);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await WhisperAssets.ExtractAsync(valid, destination, new[] { file }, progress, cancelled.Token); throw new Exception("Cancelled extraction applied"); } catch (OperationCanceledException) { }
            Require(File.Exists(Path.Combine(destination, "old")), "Cancelled install replaced old files");
            await WhisperAssets.ExtractAsync(valid, destination, new[] { file }, progress, default);
            Require(await WhisperAssets.VerifyRuntimeAsync(destination, new[] { file }, default), "Valid runtime failed");
            Require(Directory.GetDirectories(root, "runtime.backup-*").Length == 1, "Recovery backup missing");
            File.WriteAllText(Path.Combine(destination, "extra.dll"), "unexpected");
            Require(!await WhisperAssets.VerifyRuntimeAsync(destination, new[] { file }, default), "Unexpected native code accepted");
            Require(Directory.GetDirectories(root, ".whisper-extract-*").Length == 0, "Extraction stage leaked");
        });
        await Check("Whisper missing assets never launch a process", async () =>
        {
            using var engine = new WhisperEngine(Path.Combine(root, "missing"));
            try { await engine.CheckAsync("cuda", default); throw new Exception("Missing assets accepted"); } catch (IOException) { }
            Require(engine.ProcessId == 0, "Process survived failure");
        });
        if (native)
        {
            // Explicit opt-in; no microphone, TTS playback, browser or user settings.
            var cache = Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-cache");
            var installed = Path.Combine(root, "native"); Directory.CreateDirectory(Path.Combine(installed, "models"));
            await Check("Whisper pinned CPU and CUDA assets install from verified official archives", async () =>
            {
                foreach (var selected in new[] { "cpu", "cuda" })
                {
                    var zip = Path.Combine(cache, selected == "cpu" ? "cpu-b5454.zip" : "cuda12-b5454.zip");
                    Require(await ModelDownload.MatchesAsync(zip, WhisperAssets.Archive(selected), default), "Archive digest mismatch");
                    await WhisperAssets.ExtractAsync(zip, WhisperAssets.Runtime(installed, selected), WhisperAssets.RuntimeFiles(selected), progress, default);
                }
                Require(await ModelDownload.MatchesAsync(Path.Combine(cache, WhisperAssets.Model.Name), WhisperAssets.Model, default), "Model digest mismatch");
                File.Copy(Path.Combine(cache, WhisperAssets.Model.Name), WhisperAssets.ModelPath(installed));
            });
            foreach (var selected in new[] { "cuda", "cpu" })
                await Check("Whisper managed " + selected + " loads real model transcribes generated WAV and stops owned process", async () =>
                {
                    using var engine = new WhisperEngine(installed);
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    var status = await engine.CheckAsync(selected, limit.Token); int pid = engine.ProcessId;
                    Require(pid > 0 && status.Contains(selected == "cuda" ? "NVIDIA" : "CPU"), "Actual backend not ready: " + status);
                    var wav = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-native", "clock.wav")));
                    var options = new UiSettings { WhisperMode = selected, Name = "자비스" };
                    var watch = Stopwatch.StartNew(); var transcript = await engine.TryRefineAsync(WhisperWake.WithPrompt(options), wav, limit.Token) ?? throw new Exception("Idle engine skipped name check");
                    Require(transcript.Text.Contains("몇") && transcript.Text.Contains("시"), "Synthetic clock sample not recognized: " + transcript.Text);
                    Require(transcript.Words.Count > 0 && transcript.Words.All(w => double.IsFinite(w.Probability) && w.Probability is >= 0 and <= 1), "Native token confidence missing");
                    Require(WhisperWake.Decide(options, "자비서 지금 몇 시야", transcript).Wake, "Actual second recognition failed to recover name");
                    rows.Add(new { name = "Whisper native " + selected + " sample measurement", pass = true, seconds = watch.Elapsed.TotalSeconds, text = transcript.Text, words = transcript.Words });
                    Require(engine.ProcessId == pid, "Model unnecessarily restarted");
                    engine.Stop(); Require(engine.ProcessId == 0, "Server retained");
                    try { using var old = Process.GetProcessById(pid); Require(old.HasExited, "Owned server still running"); } catch (ArgumentException) { }
                });
            await Check("Whisper cancelling active CPU inference terminates the owned native server", async () =>
            {
                using var engine = new WhisperEngine(installed); await engine.CheckAsync("cpu", default); int pid = engine.ProcessId;
                var wav = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-native", "term.wav")));
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try { await engine.RefineAsync(new UiSettings { WhisperMode = "cpu" }, wav, cancel.Token); throw new Exception("Cancelled native inference completed"); } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                Require(engine.ProcessId == 0, "Cancelled server retained");
                try { using var old = Process.GetProcessById(pid); Require(old.HasExited, "Cancelled server still running"); } catch (ArgumentException) { }
            });
            await Check("Whisper server exits when its owning assistant process is forcibly terminated", async () =>
            {
                var childHome = Path.Combine(root, "job-child"); Directory.CreateDirectory(childHome);
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.WindowsRoot };
                info.ArgumentList.Add("--self-test"); info.ArgumentList.Add("--whisper-job-host");
                info.Environment["VOICE_ASSISTANT_HOME"] = childHome; info.Environment["SASARISA_WHISPER_CHECK_ROOT"] = installed;
                using var child = Process.Start(info)!;
                try
                {
                    var report = Path.Combine(childHome, "whisper-child.pid"); var watch = Stopwatch.StartNew();
                    while (!File.Exists(report)) { Require(!child.HasExited && watch.Elapsed < TimeSpan.FromSeconds(70), "Child engine did not become ready"); await Task.Delay(100); }
                    int pid = int.Parse(File.ReadAllText(report)); using var serverProcess = Process.GetProcessById(pid);
                    child.Kill(false); await child.WaitForExitAsync();
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await serverProcess.WaitForExitAsync(limit.Token);
                    Require(serverProcess.HasExited, "Job did not stop server after parent termination");
                }
                finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
            });
        }
        return failed;
    }
}
