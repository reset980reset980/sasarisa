using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class ActionChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return; } throw new Exception("Unexpected acceptance"); }
    public static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> test) { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        var dir = Path.Combine(AppPaths.WindowsRoot, "artifacts", "action-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var executable = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        await Check("Registered app create rename parse reopen and remove", () =>
        {
            var file = Path.Combine(dir, "apps.json"); var registry = new AppRegistry(file);
            var item = registry.Save("작업 노트", executable);
            Require(CommandParser.Parse("작업노트 열어 줘", registry)!.Actions.Single().Value == item.Id, "Alias not parsed");
            var renamed = registry.Save("회의 노트", executable, item.Id);
            Require(renamed.Id == item.Id && registry.Resolve("작업 노트") == null, "Rename changed ID or kept old alias");
            var reopened = new AppRegistry(file); Require(reopened.Resolve("회의노트")?.Id == item.Id, "Registration not persisted");
            reopened.Remove(item.Id); Require(new AppRegistry(file).Items.Count == 0 && File.Exists(executable), "Remove touched app or did not persist");
            return Task.CompletedTask;
        });
        await Check("App registration rejects commands duplicates and corrupt overwrite", () =>
        {
            var registry = new AppRegistry(Path.Combine(dir, "valid-apps.json")); registry.Save("업무 앱", executable);
            Reject(() => registry.Save("메모장", executable)); Reject(() => registry.Save("업무앱", executable));
            Reject(() => registry.Save("다른 앱", executable + " --test")); Reject(() => registry.Save("다른 앱", "https://example.com/run.exe"));
            var file = Path.Combine(dir, "broken-apps.json"); File.WriteAllText(file, "[{\"Id\":null}]"); var broken = new AppRegistry(file);
            Require(broken.Error != null, "Corrupt registry was silently accepted"); Reject(() => broken.Save("새 앱", executable)); Require(File.ReadAllText(file) == "[{\"Id\":null}]", "Corrupt registry overwritten");
            return Task.CompletedTask;
        });
        await Check("Failed app write preserves in-memory registry", () =>
        {
            var file = Path.Combine(dir, "locked-apps.json"); var registry = new AppRegistry(file); registry.Save("첫 앱", executable);
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => registry.Save("두 앱", executable));
            Require(registry.Items.Count == 1 && new AppRegistry(file).Items.Count == 1, "Memory changed before write succeeded");
            return Task.CompletedTask;
        });
        await Check("Undo cancels only its own persisted timer", async () =>
        {
            var file = Path.Combine(dir, "timers.json"); var timers = new TimerStore(file); var first = timers.Add(120, "first"); var second = timers.Add(180, "second"); var history = new ActionHistory();
            history.Record("second timer", _ => Task.FromResult(timers.Remove(second.Id) ? "removed" : "missing"));
            Require(await history.UndoAsync(CancellationToken.None) == "removed", "Undo failed");
            Require(new TimerStore(file).Items.Single().Id == first.Id, "Undo removed unrelated timer");
            Require(history.Describe().Contains("되돌림"), "Undone state missing");
        });
        await Check("Undo does not skip unsupported actions and retries failed reversal", async () =>
        {
            int changes = 0; var history = new ActionHistory();
            history.Record("volume", _ => { changes++; return Task.FromResult("restored"); }); history.Record("typed text", unavailable: "manual undo");
            Require((await history.UndoAsync(CancellationToken.None)).Contains("manual undo") && changes == 0, "Skipped newer non-reversible action");
            bool fail = true; history.Record("retry", _ => { if (fail) throw new InvalidOperationException("pending save"); return Task.FromResult("closed"); });
            try { await history.UndoAsync(CancellationToken.None); throw new Exception("Failure ignored"); } catch (InvalidOperationException) { }
            fail = false; Require(await history.UndoAsync(CancellationToken.None) == "closed", "Failed undo entry lost");
        });
        await Check("Undo parser and missing owned window", async () =>
        {
            Require(CommandParser.Parse("되돌려")!.Actions.Single().Kind == "undo", "Undo still sends blind Ctrl+Z");
            Require(CommandParser.Parse("방금 뭐했어")!.Actions.Single().Kind == "history", "History not parsed");
            Require(CommandParser.Parse("네가 연 창 닫아")!.Actions.Single().Kind == "close_owned", "Owned close not parsed");
            Require((await new OwnedWindow(IntPtr.Zero, 0, 0, "test").CloseAsync(CancellationToken.None)).Contains("추가로 닫지"), "Missing owned window not handled");
        });
        await Check("Volume undo refuses a changed output device without mutation", () =>
        {
            var before = VolumeControl.Capture();
            Reject(() => VolumeControl.Restore(before with { DeviceId = "missing-device-fixture" }, before));
            var after = VolumeControl.Capture(); Require(before.DeviceId == after.DeviceId && Math.Abs(before.Level - after.Level) < 0.0001, "Rejected undo altered volume");
            return Task.CompletedTask;
        });
        return failed;
    }
}
