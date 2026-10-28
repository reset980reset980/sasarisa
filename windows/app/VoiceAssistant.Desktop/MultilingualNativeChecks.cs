using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class MultilingualNativeChecks
{
    static async Task<JsonElement[]> SegmentAsync(string path, UiSettings settings, string directory, CancellationToken token)
    {
        var config = Path.Combine(directory, "config-" + settings.Activation + ".json"); File.WriteAllText(config, JsonSerializer.Serialize(settings));
        using var child = Process.Start(SpeechInput.PythonInfo("--wav", path, "--config", config))!; child.StandardInput.Close();
        var output = child.StandardOutput.ReadToEndAsync(token); var errors = child.StandardError.ReadToEndAsync(token);
        try { await child.WaitForExitAsync(token); }
        catch { if (!child.HasExited) child.Kill(true); throw; }
        var text = await output; var error = await errors;
        if (child.ExitCode != 0) throw new Exception(error.Length > 3000 ? error[..3000] : error);
        var messages = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => x.StartsWith('{')).Select(x => { using var json = JsonDocument.Parse(x); return json.RootElement.Clone(); }).ToArray();
        if (!messages.Any(x => x.GetProperty("type").GetString() == "ready")) throw new Exception("WAV worker did not become ready");
        return messages.Where(x => x.GetProperty("type").GetString() == "final").ToArray();
    }
    internal static async Task<int> RunAsync(List<object> rows)
    {
        if (Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 1;
        int failed = 0;
        void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        async Task Check(string name, Func<Task> work) { try { await work(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var folder = Path.Combine(AppPaths.Home, "samples"); Directory.CreateDirectory(folder);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180)); var token = deadline.Token;
        var options = new UiSettings { Language = "en", Name = "Jarvis", WhisperMode = "cpu", Speak = false };
        var audioPath = Path.Combine(folder, "english-synthetic.wav"); JsonElement[] segments = [];
        await Check("Native English SAPI file passes through the real Silero worker without microphone or playback", async () =>
        {
            Require(await ModelDownload.MatchesAsync(AppPaths.VadModel, MultilingualSpeech.SpeechDetector, token), "Desktop speech detector pin rejected the packaged model");
            var audio = await SapiSpeech.RenderAsync("Jarvis, tell me the time.", options, token);
            File.WriteAllBytes(audioPath, audio.Bytes);
            segments = await SegmentAsync(audioPath, options, folder, token);
            Require(segments.Length == 1 && segments[0].GetProperty("needs_transcription").GetBoolean() && segments[0].GetProperty("text").GetString() == "" && !segments[0].GetProperty("has_wake").GetBoolean(), "VAD invented text or split the short utterance");
            Require(segments[0].GetProperty("wav").GetString()!.Length > 1000, "Speech audio missing");
        });
        await Check("Native multilingual Whisper CPU recognizes the English segment and validates wake plus command", async () =>
        {
            Require(segments.Length == 1, "English segmentation prerequisite failed");
            var root = Environment.GetEnvironmentVariable("SASARISA_LANGUAGE_MODEL_ROOT") ?? throw new Exception("Set the verified existing model directory for this native check");
            using var engine = new WhisperEngine(root); var watch = Stopwatch.StartNew();
            var transcript = await engine.RefineAsync(WhisperWake.WithPrompt(options), segments[0].GetProperty("wav").GetString()!, token);
            var result = MultilingualSpeech.Decide(options, transcript, false, false);
            Require(result.Accepted && result.Wake && result.Text.Contains("time", StringComparison.OrdinalIgnoreCase), "English primary recognition did not recover the request: " + transcript.Text);
            Require(transcript.Words.Count > 0, "Native confidence absent");
            rows.Add(new { name = "Native English transcript measurement", pass = true, text = transcript.Text, command = result.Text, seconds = watch.Elapsed.TotalSeconds, words = transcript.Words });
            var pid = engine.ProcessId; engine.Stop(); Require(engine.ProcessId == 0, "Owned engine retained");
            try { using var old = Process.GetProcessById(pid); Require(old.HasExited, "Native child survived stop"); } catch (ArgumentException) { }
        });
        await Check("Native VAD rejects silent WAV input and held mode waits for the file release", async () =>
        {
            var silence = Path.Combine(folder, "silence.wav"); File.WriteAllBytes(silence, VoiceChecks.Silence(3000));
            Require((await SegmentAsync(silence, options, folder, token)).Length == 0, "Silence produced a primary request");
            var held = await SegmentAsync(audioPath, options with { Activation = "hold" }, folder, token);
            Require(held.Length == 1 && held[0].GetProperty("needs_transcription").GetBoolean(), "Held speech did not flush exactly once");
        });
        return failed;
    }
}
