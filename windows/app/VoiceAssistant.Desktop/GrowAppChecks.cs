using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    async Task<int> RunGrowAppChecksAsync()
    {
        if (string.Equals(Path.GetFullPath(AppPaths.Home).TrimEnd('\\'), Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); int failed = 0;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        settings = new UiSettings { Name = "공유시험", Speak = false, GrowTogether = "solo" };
        personal = new Personalization(Path.Combine(AppPaths.Home, Guid.NewGuid().ToString("N") + ".json")); timers = new TimerStore(Path.Combine(AppPaths.Home, "timers.json"));
        window = new MainWindow(); overlay = new OverlayWindow(); MainWindow = window; window.LoadSettings(settings); overlay.Configure(settings.Desktop);
        using var http = new HttpClient(new GrowChecks.Handler((_, _) => Task.FromResult(GrowChecks.Json(new { items = new[] { new { s = "몇시래", c = "현재 시간" }, new { s = "소리 줄여", c = "소리 키워" }, new { s = "개인 호출", c = "소리 키워" } } }))));
        grow = new GrowTogether(Path.Combine(AppPaths.Home, Guid.NewGuid().ToString("N") + ".json"), http);
        await grow.SyncAsync(settings, CancellationToken.None);
        try
        {
            await Check("Shared clock phrase runs through actual App command path and local correction vetoes it", async () =>
            {
                await SubmitAsync("몇시래", false);
                Require(lastShared?.Pair.S == "몇시래" && !busy, "Shared App action did not complete");
                await SubmitAsync("틀렸어", false);
                Require(lastShared == null && grow.Lookup("몇시래") == null && grow.Snapshot().Vetoes.Count == 1, "Actual App veto missed");
            });
            await Check("Personal learning and built-in commands outrank shared commands", () =>
            {
                personal.SaveLearned("개인 호출", "로컬", new[] { new AssistantAction("volume", Number:25) }, null, personal.Version);
                Require(ResolveWithShared("개인 호출", null, out var shared)?.Actions[0].Number == 25 && shared == null, "Pack overrode local alias");
                Require(ResolveWithShared("소리 줄여", CommandParser.Parse("소리 줄여"), out shared)?.Actions[0].Number == -10 && shared == null, "Pack overrode built-in command"); return Task.CompletedTask;
            });
            await Check("Disabled sharing does not resolve cached pack and expired veto does not discard a mapping", () =>
            {
                settings.GrowTogether = "off"; Require(ResolveWithShared("소리 줄여", null, out _) == null, "Off resolved cached command");
                lastShared = (new SharedPair("소리줄여", "소리 키워"), DateTimeOffset.UtcNow.AddMinutes(-1));
                ResolveWithShared("아니", null, out _); Require(grow.Snapshot().Vetoes.Count == 1, "Expired veto applied");
                settings.GrowTogether = "solo"; return Task.CompletedTask;
            });
            await Check("Remote App learning cannot contribute while equivalent local learning can", async () =>
            {
                settings.GrowTogether = "together"; var plan = CommandParser.Parse("소리 줄여")!;
                await new RemoteExecution(CancellationToken.None).RunAsync(() => { RecordSharedLearning("소리 좀 줄여", plan); return Task.CompletedTask; });
                Require(grow.Snapshot().Pending.Count == 0, "Remote phrase queued for sharing");
                RecordSharedLearning("소리 좀 줄여", plan); Require(grow.Snapshot().Pending.Count == 1, "Local learning not queued");
            });
            await Check("Turning sharing off drains the actual App sync and rejects its late network response", async () =>
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken requestToken = default;
                using var delayed = new HttpClient(new GrowChecks.Handler(async (_, token) => { requestToken = token; entered.SetResult(); await release.Task; return GrowChecks.Json(new { items = new[] { new { s = "잠시만", c = "현재 시간" } } }); }));
                grow = new GrowTogether(Path.Combine(AppPaths.Home, Guid.NewGuid().ToString("N") + ".json"), delayed);
                settings.GrowTogether = "solo"; RestartGrow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                settings.GrowTogether = "off"; RestartGrow(); Require(requestToken.IsCancellationRequested, "App mode change did not cancel HTTP"); release.SetResult();
                await growTask.WaitAsync(TimeSpan.FromSeconds(3)); Require(grow.Snapshot().Pack.Count == 0 && growStop == null, "Old App generation remained active");
            });
        }
        finally { await StopGrowAsync(); StopTurn(); overlay.Close(); window.Close(); }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "grow-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
