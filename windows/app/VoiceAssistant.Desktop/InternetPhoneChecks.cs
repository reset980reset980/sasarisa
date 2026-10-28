using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VoiceAssistant.Desktop;

public partial class App
{
    bool IsolatedPhoneProfile() => !string.Equals(Path.GetFullPath(AppPaths.Home).TrimEnd('\\'), Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    void InitializeInternetPhoneFixture()
    {
        settings = new UiSettings { Name = "폰시험", Speak = false, ProRemote = false };
        personal = new Personalization(Path.Combine(AppPaths.Home, "personal.json")); timers = new TimerStore(Path.Combine(AppPaths.Home, "timers.json"));
        window = new MainWindow(); overlay = new OverlayWindow(allowPresentation: false); MainWindow = window; window.LoadSettings(settings); overlay.Configure(settings.Desktop);
    }
    async Task<int> RunInternetPhoneAppChecksAsync(bool ai = false)
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture();
        var rows = new List<object>(); int failed = 0, calls = 0;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        try
        {
            internetPhone = new InternetPhone(); await internetPhone.OpenAsync();
            var connection = internetPhone;
            var pump = new RemotePump(connection, new RemoteLedger(Path.Combine(AppPaths.Home, "internet-phone-receipts.json")),
                (command, token) => { System.Threading.Interlocked.Increment(ref calls); return Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, token, true)).Task.Unwrap(); },
                text => connection.Status = text);
            internetPhoneTask = Task.Run(() => pump.RunAsync(connection.Token));
            using var phone = new PhoneProbe(connection.Url); await phone.Pair(connection.Code);
            await Check("Internet HTTPS reaches actual App clock without Pro or a LAN listener", async () =>
            { var result = await phone.Command("폰시험 지금 몇 시야"); Require(result.Contains("시") && result.Contains("답변 읽기가 꺼져") && !result.Contains("확인하지 못") && !busy && !settings.ProRemote && phoneServer == null, result); });
            await Check("Internet paired phone identifies a normal App connection", async () =>
            { var status = (await phone.Request("/api/status")).Data; Require(status.GetProperty("client").GetProperty("kind").GetString() == "standard", "Incorrect client profile"); });
            await Check("Internet phone creates lists and cancels actual App timers", () => VerifyPhoneTimersAsync(phone));
            await Check("Internet phone reaches actual Windows speech playback with silent test volume", () => VerifyPhoneSpeechAsync(phone));
            if (ai) await Check("Internet phone sends a real general question through the saved Codex CLI account", async () =>
            {
                var saved = settings;
                try { settings = settings with { BrainProvider = "codex" }; var result = await phone.Command("2 더하기 3은 얼마야? 계산 결과 숫자만 짧게 답해."); Require(result.Contains("5") && !result.Contains("실패") && !result.Contains("오류"), result); }
                finally { settings = saved; }
            });
            await Check("Internet redelivery cannot execute a completed App command twice", async () =>
            {
                var id = Guid.NewGuid().ToString("N"); await phone.Request("/api/command", new { id, text = "지금 몇 시야" }); var first = await phone.Result(id); var count = calls;
                await phone.Request("/api/command", new { id, text = "메모장 열어" }); Require(await phone.Result(id) == first && count == calls, "Duplicate executed");
            });
            await Check("Internet phone retains PC-review restrictions", async () =>
            { Require((await phone.Command("받아쓰기 시작")).Contains("PC에서") && !dictating && !busy, "PC review bypassed"); });
            await Check("Internet phone refuses to interrupt ongoing PC work", async () =>
            { busy = true; try { Require((await phone.Command("지금 몇 시야")).Contains("다른 작업"), "Busy work interrupted"); } finally { busy = false; } });
            await Check("Internet stop cancels actual queued App execution", async () =>
            {
                await executionGate.WaitAsync();
                try
                {
                    var id = Guid.NewGuid().ToString("N"); await phone.Request("/api/command", new { id, text = "지금 몇 시야" });
                    for (int i = 0; i < 150 && !busy; i++) await Task.Delay(30);
                    Require(busy, "App action never started"); await phone.Request("/api/stop", new { });
                    Require((await phone.Result(id)).Contains("중지") && !busy, "App action survived stop");
                }
                finally { executionGate.Release(); }
            });
            await Check("Internet disconnect drains App execution and revokes the phone at the relay", async () =>
            {
                await executionGate.WaitAsync();
                try
                {
                    await phone.Request("/api/command", new { id = Guid.NewGuid().ToString("N"), text = "지금 몇 시야" });
                    for (int i = 0; i < 150 && !busy; i++) await Task.Delay(30);
                    Require(busy, "App action never started"); await StopPhoneAsync();
                    Require(!busy && internetPhone == null && (await phone.Request("/api/status")).Status == HttpStatusCode.Unauthorized, "Request or phone survived disconnect");
                }
                finally { executionGate.Release(); }
            });
        }
        catch (Exception ex) { failed++; rows.Add(new { name = "Internet fixture setup/lifecycle", pass = false, error = ex.Message }); }
        finally { await StopPhoneAsync(); StopTurn(); overlay.Close(); window.Close(); }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "internet-phone-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
    async Task ShowInternetPhonePreviewAsync()
    {
        if (!IsolatedPhoneProfile()) { Shutdown(2); return; }
        InitializeInternetPhoneFixture();
        internetPhone = new InternetPhone(preview: true); await internetPhone.OpenAsync(); var connection = internetPhone;
        var pump = new RemotePump(connection, new RemoteLedger(Path.Combine(AppPaths.Home, "internet-phone-receipts.json")),
            (command, token) => command.Text == "지금 몇 시야" ? Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, token, true)).Task.Unwrap() : Task.FromResult("인터넷 시험에서는 ‘지금 몇 시야’만 실행합니다."),
            text => connection.Status = text);
        internetPhoneTask = Task.Run(() => pump.RunAsync(connection.Token));
        Directory.CreateDirectory(AppPaths.Home);
        var file = Path.Combine(AppPaths.Home, "preview-connection.json"); File.WriteAllText(file, JsonSerializer.Serialize(new { url = connection.Url, code = connection.Code, onlyClock = true }));
        window.QuitRequested += Quit; window.Closing += OnWindowClosing; window.Show();
        phoneWindow = new PhoneWindow(_ => Task.CompletedTask, StopPhoneAsync, () => phoneServer, () => Task.CompletedTask, () => internetPhone) { Owner = window, Title = "인터넷 시험 · 시계 명령만 실행" };
        phoneWindow.PhoneHeading.Text = "인터넷 시험 · 시계 명령만 실행"; phoneWindow.Show();
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        deadline.Tick += async (_, _) => { deadline.Stop(); await StopPhoneAsync(); File.Delete(file); Shutdown(); }; deadline.Start();
    }
}
