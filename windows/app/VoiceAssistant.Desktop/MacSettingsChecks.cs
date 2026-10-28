using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal static class MacSettingsChecks
{
    internal static UiSettings Current() => new()
    {
        Name = "윈도우", Device = "win-mic", VoiceId = "win-voice", OutputId = "win-output", SpeechRate = 2, SpeechVolume = 37,
        BrainProvider = "codex", BrainModel = "win-model", BrainUrl = "", ProjectDirectory = "C:\\win-project", WhisperMode = "external", WhisperUrl = "http://127.0.0.1:12345",
        Desktop = new() { SetupComplete = true, OverlayScreen = "win-screen", StartupListening = "off" }, Text = new() { CustomWords = ["기존단어"], RemoveFillers = false }
    };
    static void Require(bool ok, string text) { if (!ok) throw new Exception(text); }
    static void Reject(Action action)
    { try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException) { return; } throw new Exception("Invalid Mac settings accepted"); }
    internal static int Run(List<object> rows)
    {
        int failed = 0;
        void Check(string name, Action action) { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        Check("Mac common settings convert without mutating the current profile or enabling remote access", () =>
        {
            var current = Current(); var before = JsonSerializer.Serialize(current);
            var source = JsonSerializer.Serialize(new
            {
                assistantName = "코코", language = "ko", activation = "hold", userName = "대표님", greetingText = "안녕하세요 {user}", bootTitle = "COCO", bootSubtitle = "{assistant} 준비", bootStyle = "calm", hudPosition = "top", showIdleOrb = true,
                greetPhrases = new[] { "아빠 왔다" }, greetOpen = new[] { "https://example.com/" }, silenceSec = 1.7, followUpSec = 45, pauseInCalls = false, bargeIn = "wake", polishDictation = true,
                customWords = new[] { "특수명사", "특수명사" }, fillerWords = Array.Empty<string>(), learnedWakes = new Dictionary<string, string[]> { ["코코"] = ["고고"] }, wakeWords = new[] { "헤이 코코" },
                inputDevice = "mac-mic", outputDevice = "mac-output", hotkey = "rightOption", sayVoice = "apple-voice", sayRate = 270, browserApp = "Safari", apps = new { note = "Notes" },
                proRemote = true, growTogether = "together", freeMode = true, brainAccess = "full", whisperModel = "/Mac/model.bin", whisperServer = "/Mac/whisper-server"
            });
            var converted = MacSettingsTransfer.Parse(source, current); var value = converted.Settings;
            Require(JsonSerializer.Serialize(current) == before, "Current settings mutated");
            Require(value.Name == "코코" && value.Activation == "hold" && value.Desktop.UserName == "대표님" && value.Desktop.GreetingTitle == "COCO" && value.Desktop.GreetingSubtitle == "{assistant} 준비" && value.Desktop.GreetingStyle == "calm" && value.Desktop.OverlayPosition == "top" && value.Desktop.ShowIdleOrb, "Personalization lost");
            Require(value.SilenceSeconds == 1.7 && value.FollowUpSeconds == 45 && !value.PauseInCalls && value.BargeIn == "wake" && value.Text.PolishDictation && value.Text.RemoveFillers && value.Text.FillerWords.Length == 4 && value.Text.CustomWords.SequenceEqual(new[] { "특수명사" }), "Speech/text preferences lost");
            Require(value.LearnedWakes["코코"].Contains("고고") && value.LearnedWakes["코코"].Contains("헤이 코코"), "Wake aliases lost");
            Require(value.Device == current.Device && value.VoiceId == current.VoiceId && value.OutputId == current.OutputId && value.SpeechRate == current.SpeechRate && value.SpeechVolume == current.SpeechVolume && value.ProjectDirectory == current.ProjectDirectory && value.WhisperUrl == current.WhisperUrl && value.Desktop.WakeHotkey == current.Desktop.WakeHotkey && value.Desktop.StartupListening == "off" && !value.ProRemote && value.GrowTogether == "off", "Mac platform/access settings changed local choices");
            Require(converted.Notices.Any(n => n.Contains("자유 모드")) && converted.Notices.Any(n => n.Contains("휴대폰")), "Excluded settings hidden");
        });
        Check("Partial Mac files retain omitted settings and expose language and unknown-field exclusions", () =>
        {
            var current = Current(); current.LearnedWakes["윈도우"] = ["윈도"]; var before = JsonSerializer.Serialize(current);
            var result = MacSettingsTransfer.Parse("{\"language\":\"en\",\"futureField\":\"fixture-secret\"}", current);
            Require(JsonSerializer.Serialize(result.Settings) == before && result.Notices.Any(n => n.Contains("한국어")) && result.Notices.Any(n => n.Contains("1개")), "Partial or excluded fields changed data");
            Require(!JsonSerializer.Serialize(result).Contains("fixture-secret"), "Excluded value leaked");
        });
        Check("Mac provider IDs, default endpoints and Claude profiles map to actual Windows providers", () =>
        {
            foreach (var id in new[] { "claude-code", "codex", "aside", "combo", "anthropic", "openai", "gemini", "openrouter", "groq", "ollama", "lmstudio", "openai-compatible" })
            {
                var expected = id == "claude-code" ? "claude" : id == "openai-compatible" ? "compatible" : id;
                var result = MacSettingsTransfer.Parse(JsonSerializer.Serialize(new { brainProvider = id, brainModel = "fixture-model", brainBaseURL = "", claudeModel = "sonnet" }), Current()).Settings;
                Require(result.BrainProvider == expected && result.BrainModel == (expected == "claude" ? "sonnet" : "fixture-model") && result.ProviderProfiles[expected].Model == result.BrainModel && result.BrainUrl == ApiProviders.DefaultUrl(expected), "Provider mapping failed: " + id);
                Require(result.ProviderProfiles["claude"].Model == "sonnet", "Claude profile missing");
            }
        });
        Check("Mac connections remain opt-in and platform devices cannot be imported even when selected", () =>
        {
            var current = Current(); var imported = MacSettingsTransfer.Parse("{\"assistantName\":\"코코\",\"brainProvider\":\"ollama\",\"brainBaseURL\":\"http://127.0.0.1:11435\",\"inputDevice\":\"mac-mic\",\"tts\":\"openai\",\"ttsVoice\":\"alloy\"}", current).Settings;
            var standard = SettingsTransfer.Prepare(imported, current, false, false);
            Require(standard.Name == "코코" && standard.BrainProvider == current.BrainProvider && standard.BrainModel == current.BrainModel && standard.Tts == current.Tts, "Connection imported without selection");
            var explicitImport = SettingsTransfer.Prepare(imported, current, true, true);
            Require(explicitImport.BrainProvider == "ollama" && explicitImport.BrainUrl == "http://127.0.0.1:11435" && explicitImport.Tts.Provider == "openai" && explicitImport.Device == current.Device && explicitImport.VoiceId == current.VoiceId && explicitImport.Desktop.StartupListening == "off", "Explicit connection import or platform preservation failed");
        });
        Check("ElevenLabs conversion retains voice options while excluding inline keys and key-file paths", () =>
        {
            var source = "{\"tts\":\"elevenlabs\",\"elevenLabs\":{\"voiceId\":\"voice_123\",\"model\":\"fixture-model\",\"speed\":1.1,\"apiKey\":\"fixture-secret\",\"apiKeyFile\":\"/private/fixture.env\"}}";
            var result = MacSettingsTransfer.Parse(source, Current()); var voice = result.Settings.Tts;
            Require(voice.Provider == "elevenlabs" && voice.Voice == "voice_123" && voice.Model == "fixture-model" && voice.Speed == 1.1, "Voice options lost");
            var output = JsonSerializer.Serialize(result) + SettingsTransfer.Serialize(result.Settings) + SettingsTransfer.Describe(Current(), result.Settings);
            Require(!output.Contains("fixture-secret") && !output.Contains("/private/fixture.env") && result.Notices.Any(n => n.Contains("API 키")), "Credential copied or exclusion hidden");
        });
        Check("Mac say, OpenAI, Azure and local HTTP voice settings map without importing Apple voice IDs", () =>
        {
            var current = Current();
            foreach (var provider in new[] { "say", "openai", "azure", "http" })
            {
                var result = MacSettingsTransfer.Parse(JsonSerializer.Serialize(new { tts = provider, ttsVoice = provider == "azure" ? "ko-KR-SunHiNeural" : "alloy", ttsModel = "tts-model", ttsRegion = "koreacentral", ttsURL = "http://127.0.0.1:5002/tts", sayVoice = "apple-voice", sayRate = 250 }), current).Settings;
                Require(result.Tts.Provider == (provider == "say" ? "windows" : provider) && result.VoiceId == current.VoiceId && result.SpeechRate == current.SpeechRate, "Voice provider/Apple ID changed");
                if (provider == "http") Require(result.Tts.Url == "http://127.0.0.1:5002/tts", "HTTP endpoint lost");
            }
        });
        Check("Legacy Mac title and Korean default wake compatibility resolve without hidden name replacement", () =>
        {
            var result = MacSettingsTransfer.Parse("{\"assistantName\":\"Sasarisa\",\"bootTitle\":\"M A C   B U T L E R\",\"wakeWords\":[]}", Current()).Settings;
            Require(result.Name == "Sasarisa" && result.Desktop.GreetingTitle == "" && result.LearnedWakes["Sasarisa"].Contains("자비스"), "Mac resolved defaults changed");
            var custom = MacSettingsTransfer.Parse("{\"wakeWords\":[\"코코\",\"음\"]}", Current());
            Require(custom.Settings.LearnedWakes["윈도우"].Contains("코코") && !custom.Settings.LearnedWakes["윈도우"].Contains("음") && custom.Notices.Any(n => n.Contains("1개")), "Unsupported wake omission hidden");
        });
        Check("Settings review now describes imported gain and name-hint changes", () =>
        {
            var current = Current(); var after = SettingsTransfer.Copy(current); after.InputGain = 3; after.WakeHints = true;
            var description = SettingsTransfer.Describe(current, after);
            Require(description.Contains("인식 입력 증폭") && description.Contains("호출 이름 인식 단서") && description.Contains("켜짐"), "Speech input changes hidden");
        });
        Check("Mac malformed formats duplicates nulls invalid ranges and connection credentials are rejected", () =>
        {
            foreach (var text in new[] { "{}", "[]", "{broken", "{\"assistantName\":null}", "{\"assistantName\":\"코코\",\"AssistantName\":\"duplicate\"}", "{\"silenceSec\":\"1.2\"}", "{\"silenceSec\":0.1}", "{\"followUpSec\":999}", "{\"brainProvider\":\"shell\"}", "{\"brainProvider\":\"compatible\",\"brainBaseURL\":\"https://user:fixture-secret@example.com/v1\"}", "{\"brainProvider\":\"compatible\",\"brainBaseURL\":\"http://remote.example/v1\"}", "{\"brainProvider\":\"compatible\",\"brainBaseURL\":\"https://example.com/v1?key=fixture-secret\"}" }) Reject(() => MacSettingsTransfer.Parse(text, Current()));
            Reject(() => MacSettingsTransfer.Parse(new string('x', SettingsTransfer.MaxBytes + 1), Current()));
            Reject(() => MacSettingsTransfer.Parse(SettingsTransfer.Serialize(Current()), Current()));
        });
        Check("Mac file parsing is bounded and does not replace a file or touch Windows settings", () =>
        {
            var folder = Path.Combine(AppPaths.WindowsRoot, "artifacts", "mac-settings-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); var path = Path.Combine(folder, "config.json");
            const string source = "{\"assistantName\":\"코코\",\"customWords\":[\"명사\"]}"; File.WriteAllText(path, source);
            Require(MacSettingsTransfer.Read(path, Current()).Settings.Name == "코코" && File.ReadAllText(path) == source, "Mac source changed");
            File.WriteAllText(path, new string('x', SettingsTransfer.MaxBytes + 1)); Reject(() => MacSettingsTransfer.Read(path, Current()));
        });
        return failed;
    }
}
