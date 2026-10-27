using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal static class ListeningStateChecks
{
    public static int Run(List<object> rows)
    {
        int failed = 0;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        void Check(string name, Action action)
        { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        string StatePath() => Path.Combine(AppPaths.WindowsRoot, "artifacts", "listening-tests", Guid.NewGuid().ToString("N"), "state.json");
        Check("Listening intent survives restart and explicit stop replaces it atomically", () =>
        {
            var path = StatePath(); var state = new ListeningState(path);
            Require(!state.ShouldStart("restore", true) && state.Error == null, "Missing state started capture");
            state.Remember(true); state = new ListeningState(path);
            Require(state.ShouldStart("restore", true), "Start lost on restart");
            state.Remember(false); state = new ListeningState(path);
            Require(!state.ShouldStart("restore", true) && new ListeningState(path + ".previous").Requested, "Stop or backup lost");
            Require(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Temporary file leaked");
        });
        Check("Startup listening modes respect readiness and explicit launch without rewriting intent", () =>
        {
            var state = new ListeningState(StatePath());
            Require(state.ShouldStart("always", true) && !state.ShouldStart("off", true) && !state.ShouldStart("unknown", true), "Mode ignored");
            state.Remember(true);
            foreach (var mode in new[] { "restore", "always", "off" })
            {
                Require(!state.ShouldStart(mode, false) && !state.ShouldStart(mode, false, true), "Incomplete setup started capture");
                Require(state.ShouldStart(mode, true, true), "Explicit launch ignored");
            }
            Require(!state.ShouldStart("off", true) && state.ShouldStart("restore", true), "Mode evaluation changed user intent");
        });
        Check("Corrupt oversized and unsupported listening state stays stopped and can be repaired", () =>
        {
            foreach (var bad in new[] { "{", "null", "{\"Version\":2,\"Requested\":true}", "{\"Version\":1,\"Requested\":\"yes\"}", new string('x', 4097) })
            {
                var path = StatePath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, bad);
                var state = new ListeningState(path);
                Require(state.Error != null && !state.ShouldStart("restore", true), "Bad state started capture");
                state.Remember(true);
                Require(state.Error == null && new ListeningState(path).ShouldStart("restore", true), "Explicit repair failed");
            }
        });
        Check("Failed listening persistence keeps previous disk and memory state", () =>
        {
            var path = StatePath(); var state = new ListeningState(path); state.Remember(true); var before = File.ReadAllText(path);
            bool rejected = false;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { try { state.Remember(false); } catch (IOException) { rejected = true; } }
            Require(rejected && state.Requested && File.ReadAllText(path) == before, "Failed save changed intent");
            Require(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Failed save leaked stage");
        });
        Check("Old settings default to restore and invalid startup listening values are rejected", () =>
        {
            var old = JsonSerializer.Deserialize<UiSettings>("{\"Name\":\"자비스\",\"Desktop\":{\"SetupComplete\":true}}", SettingsStore.Json)!;
            Require(old.Desktop.StartupListening == "restore", "Old setting default changed");
            bool rejected = false;
            try { SettingsStore.Validate(old with { Desktop = old.Desktop with { StartupListening = "invalid" } }); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Invalid mode accepted");
        });
        Check("Settings import preserves local startup listening unless device preferences are selected", () =>
        {
            var current = new UiSettings { Desktop = new() { StartupListening = "off" } };
            var imported = SettingsTransfer.Parse(SettingsTransfer.Serialize(new UiSettings { Desktop = new() { StartupListening = "always" } }));
            Require(SettingsTransfer.Prepare(imported, current, false, false).Desktop.StartupListening == "off", "Unselected local preference replaced");
            var selected = SettingsTransfer.Prepare(imported, current, false, true);
            Require(selected.Desktop.StartupListening == "always" && SettingsTransfer.Describe(current, selected).Contains("앱 실행 시 듣기"), "Explicit import or review lost");
        });
        return failed;
    }
}
