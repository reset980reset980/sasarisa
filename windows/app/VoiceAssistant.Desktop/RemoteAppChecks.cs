using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    // Exercises the actual App -> SubmitAsync -> action/result path, with only harmless fixed
    // commands. Requires an isolated self-test profile and never starts the remote HTTP pump.
    async Task<int> RunRemoteAppChecksAsync()
    {
        if (string.Equals(Path.GetFullPath(AppPaths.Home).TrimEnd('\\'), Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); int failed = 0;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        settings = new UiSettings { Name = "원격시험", Speak = false, ProRemote = true };
        personal = new Personalization(Path.Combine(AppPaths.Home, "personal.json")); timers = new TimerStore(Path.Combine(AppPaths.Home, "timers.json"));
        window = new MainWindow(); overlay = new OverlayWindow(); MainWindow = window; window.LoadSettings(settings); overlay.Configure(settings.Desktop);
        RemoteCommand Command(string text) => new(Guid.NewGuid().ToString("N"), text, new string('F', 64), CancellationToken.None);
        try
        {
            await Check("Remote App command reaches real clock execution and returns its displayed answer", async () =>
            {
                var result = await RunRemoteCommandAsync(Command("원격시험 지금 몇 시야"), CancellationToken.None);
                Require(result.Contains("시") && !result.Contains("확인하지 못") && !busy, "Actual clock response missing: " + result);
            });
            await Check("Remote App rejects local-review commands before showing a modal or touching another app", async () =>
            {
                foreach (var text in new[] { "받아쓰기 시작", "카톡 초안 전송" })
                {
                    var result = await RunRemoteCommandAsync(Command(text), CancellationToken.None);
                    Require(result.Contains("PC에서") && !busy && !dictating, "Remote review not held: " + result);
                }
            });
            await Check("Remote App respects disabled access and ongoing local work", async () =>
            {
                settings.ProRemote = false;
                Require((await RunRemoteCommandAsync(Command("지금 몇 시야"), CancellationToken.None)).Contains("꺼져"), "Disabled remote executed");
                settings.ProRemote = true; busy = true;
                Require((await RunRemoteCommandAsync(Command("지금 몇 시야"), CancellationToken.None)).Contains("다른 작업"), "Remote interrupted local work");
                busy = false;
            });
            await Check("A new local command cancels the actual remote App turn without capturing the local reply", async () =>
            {
                await executionGate.WaitAsync();
                Task<string>? remote = null; Task? local = null;
                try
                {
                    remote = RunRemoteCommandAsync(Command("지금 몇 시야"), CancellationToken.None);
                    Require(busy && !remote.IsCompleted, "Remote did not reach action gate");
                    local = SubmitAsync("지금 몇 시야", false);
                }
                finally { executionGate.Release(); }
                await local!; var result = await remote!;
                Require(result.Contains("중지") && !result.Contains("현재 시각") && !busy, "Local priority or reply isolation failed: " + result);
            });
            await Check("Cancelling the phone request reaches the actual App action gate", async () =>
            {
                using var stop = new CancellationTokenSource(); await executionGate.WaitAsync();
                try
                {
                    var pending = RunRemoteCommandAsync(Command("지금 몇 시야"), stop.Token);
                    Require(!pending.IsCompleted, "Expected queued action"); stop.Cancel();
                    Require((await pending.WaitAsync(TimeSpan.FromSeconds(3))).Contains("중지") && !busy, "Phone cancellation did not stop App turn");
                }
                finally { executionGate.Release(); }
            });
        }
        finally { StopTurn(); overlay.Close(); window.Close(); }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "remote-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
