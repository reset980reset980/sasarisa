using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceAssistant.Desktop;

internal static class SettingsTransferChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException) { return; } throw new Exception("Invalid transfer accepted"); }
    public static int Run(List<object> rows)
    {
        int failed = 0;
        void Check(string name, Action test) { try { test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        Check("Settings export and import preserve nested preferences without credential fields", () =>
        {
            var source = new UiSettings { Name = "아리", Desktop = new() { GreetingText = "안녕하세요 {user}", GreetingUrls = new[] { "https://example.com" } }, Text = new() { CustomWords = new[] { "윈도우" } }, ProviderProfiles = new() { ["openai"] = new("model-id", "https://api.openai.com/v1") } };
            var json = SettingsTransfer.Serialize(source); var copy = SettingsTransfer.Parse(json);
            Require(copy.Name == source.Name && copy.Text.CustomWords.Single() == "윈도우" && copy.Desktop.GreetingUrls.Single() == "https://example.com" && copy.ProviderProfiles["openai"].Model == "model-id", "Nested settings lost");
            Require(!json.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) && !json.Contains("Password", StringComparison.OrdinalIgnoreCase), "Credential field exported");
            copy.Desktop.GreetingText = "changed"; Require(source.Desktop.GreetingText == "안녕하세요 {user}", "Copy aliased source");
        });
        Check("Settings transfer rejects other formats nulls duplicates secrets and oversized files", () =>
        {
            var json = SettingsTransfer.Serialize(new UiSettings());
            foreach (var bad in new[] { "{}", "{broken", json.Replace("\"Version\": 1", "\"Version\": 2"), json.Replace("\"Settings\": {", "\"Settings\": {\"ApiKey\":\"do-not-import\","), json.Replace("\"Settings\": {", "\"Settings\": {\"name\":\"duplicate\","), json.Replace("\"GreetingText\": \"\"", "\"GreetingText\": null"), json.Replace("\"Threads\": 2", "\"Threads\": 99"), new string('x', SettingsTransfer.MaxBytes + 1) }) Reject(() => SettingsTransfer.Parse(bad));
            Reject(() => SettingsTransfer.Serialize(new UiSettings { BrainUrl = "http://127.0.0.1:1234?key=secret" }));
        });
        Check("Settings import keeps local connections and devices unless explicitly selected", () =>
        {
            var current = new UiSettings { Device = "local-mic", VoiceId = "local-voice", OutputId = "local-out", BrainProvider = "codex", BrainModel = "local-model", ProjectDirectory = "C:\\local-project", Desktop = new() { SetupComplete = true, OverlayScreen = "local-screen" } };
            var imported = new UiSettings { Name = "아리", Device = "other-mic", BrainProvider = "ollama", BrainUrl = "http://127.0.0.1:11435", Desktop = new() { SetupComplete = false, OverlayPosition = "center", OverlayScreen = "other-screen" } };
            var standard = SettingsTransfer.Prepare(imported, current, false, false);
            Require(standard.Name == "아리" && standard.BrainProvider == "codex" && standard.BrainModel == "local-model" && standard.Device == "local-mic" && standard.VoiceId == "local-voice" && standard.OutputId == "local-out" && standard.ProjectDirectory == "C:\\local-project" && standard.Desktop.SetupComplete && standard.Desktop.OverlayPosition == "center" && standard.Desktop.OverlayScreen == "local-screen", "Local preferences changed unexpectedly");
            var all = SettingsTransfer.Prepare(imported, current, true, true); Require(all.BrainProvider == "ollama" && all.Device == "other-mic" && all.Desktop.OverlayScreen == "other-screen" && all.Desktop.SetupComplete, "Explicit restoration failed");
            Require(SettingsTransfer.Describe(current, all).Contains("AI 서버 주소") && SettingsTransfer.Describe(current, all).Contains("호출 이름"), "Connection or name hidden from review");
        });
        Check("Settings file replacement preserves previous content and survives failed writes", () =>
        {
            var folder = Path.Combine(AppPaths.WindowsRoot, "artifacts", "transfer-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); var path = Path.Combine(folder, "settings.json");
            SettingsTransfer.Write(path, new UiSettings { Name = "처음" }); SettingsTransfer.Write(path, new UiSettings { Name = "다음" });
            Require(SettingsTransfer.Read(path).Name == "다음" && SettingsTransfer.Read(path + ".previous").Name == "처음", "Previous export not preserved");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => SettingsTransfer.Write(path, new UiSettings { Name = "실패" }));
            Require(SettingsTransfer.Read(path).Name == "다음" && Directory.GetFiles(folder, "*.tmp").Length == 0, "Failed write damaged export or leaked temp");
        });
        return failed;
    }
}
