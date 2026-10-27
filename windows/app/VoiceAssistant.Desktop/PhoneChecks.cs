using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed class PhoneProbe(string url) : IDisposable
{
    readonly HttpClient client = new() { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
    public string Token = "";
    public async Task<(HttpStatusCode Status, JsonElement Data)> Request(string path, object? body = null, string? origin = null, string? host = null, bool auth = true)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("X-Sasarisa-Client", "1"); request.Headers.Add("Origin", origin ?? url);
        if (host != null) request.Headers.Host = host;
        if (auth && Token.Length > 0) request.Headers.Authorization = new("Bearer", Token);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.Clone());
    }
    public async Task Pair(string code)
    {
        var result = await Request("/api/pair", new { code });
        if (result.Status != HttpStatusCode.OK) throw new Exception("Pair failed: " + result.Data);
        Token = result.Data.GetProperty("token").GetString()!;
    }
    public async Task<string> Command(string text)
    {
        var id = Guid.NewGuid().ToString("N"); var result = await Request("/api/command", new { id, text });
        if (result.Status != HttpStatusCode.OK) throw new Exception("Command failed: " + result.Data);
        return await Result(id);
    }
    public async Task<string> Result(string id)
    {
        for (int i = 0; i < 1000; i++)
        {
            var row = await Request("/api/result?id=" + id);
            if (row.Status != HttpStatusCode.OK) throw new Exception("Result failed: " + row.Data);
            if (!row.Data.GetProperty("pending").GetBoolean()) return row.Data.GetProperty("result").GetString()!;
            await Task.Delay(30);
        }
        throw new TimeoutException("Phone result missing");
    }
    public void Dispose() => client.Dispose();
}

internal static class PhoneChecks
{
    internal static PhoneNetwork Loopback => new("test", IPAddress.Loopback, IPAddress.Parse("255.0.0.0"));
    public static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0, calls = 0;
        var folder = Path.Combine(AppPaths.Home, "phone-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new PhoneServer(Loopback, Path.Combine(folder, "receipts.json"), async (command, token) =>
        {
            Interlocked.Increment(ref calls);
            if (command.Text == "wait") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return "reply:" + command.Text;
        });
        await server.StartAsync(0); using var probe = new PhoneProbe(server.Url);
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        await Check("Free phone exposes only same-subnet IPv4 and rejects public peers", () =>
        {
            var network = new PhoneNetwork("LAN", IPAddress.Parse("192.168.68.101"), IPAddress.Parse("255.255.255.0"));
            Require(network.Contains(IPAddress.Parse("192.168.68.12")) && !network.Contains(IPAddress.Parse("192.168.1.2")) && !network.Contains(IPAddress.Parse("8.8.8.8")) && !network.Contains(IPAddress.IPv6Loopback), "Subnet boundary failed"); return Task.CompletedTask;
        });
        await Check("Free phone page and assets load with no cache and restrictive CSP", async () =>
        {
            using var client = new HttpClient();
            foreach (var path in new[] { "/", "/phone.js", "/phone.css" })
            {
                using var response = await client.GetAsync(server.Url + path); var text = await response.Content.ReadAsStringAsync();
                Require(response.IsSuccessStatusCode && text.Length > 200 && response.Headers.CacheControl?.NoStore == true && response.Headers.Contains("Content-Security-Policy"), "Asset/header missing: " + path);
            }
        });
        await Check("Free phone blocks unauthenticated commands and incorrect pairing code", async () =>
        {
            Require((await probe.Request("/api/command", new { id = Guid.NewGuid().ToString("N"), text = "hello" })).Status == HttpStatusCode.Unauthorized && calls == 0, "Unauthenticated action");
            Require((await probe.Request("/api/pair", new { code = "wrong" })).Status == HttpStatusCode.Unauthorized, "Wrong code accepted");
            await probe.Pair(server.Code); Require(server.Connected == 1, "Pair not counted");
        });
        await Check("Free phone rejects cross-origin requests and DNS rebinding hosts", async () =>
        {
            Require((await probe.Request("/api/status", origin: "https://example.com")).Status == HttpStatusCode.Forbidden, "Foreign origin accepted");
            Require((await probe.Request("/api/status", host: "evil.example")).Status == HttpStatusCode.Forbidden, "Foreign host accepted");
            using var client = new HttpClient(); using var response = await client.PostAsJsonAsync(server.Url + "/api/pair", new { code = server.Code });
            Require(response.StatusCode == HttpStatusCode.Forbidden, "Missing client/origin headers accepted");
        });
        await Check("Free phone validates command IDs, body size and length before execution", async () =>
        {
            var count = calls;
            Require((await probe.Request("/api/command", new { id = "invalid", text = "hello" })).Status == HttpStatusCode.BadRequest, "Bad id accepted");
            Require((await probe.Request("/api/command", new { id = Guid.NewGuid().ToString("N"), text = new string('가', 301) })).Status == HttpStatusCode.BadRequest && calls == count, "Long command accepted");
            var oversized = await probe.Request("/api/command", new { id = Guid.NewGuid().ToString("N"), text = new string('x', 5000) });
            Require(oversized.Status is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge && calls == count, "Oversized body accepted");
        });
        await Check("Free phone persists claims and never reexecutes duplicate request IDs", async () =>
        {
            var id = Guid.NewGuid().ToString("N"); var count = calls;
            await probe.Request("/api/command", new { id, text = "hello" });
            Require(await probe.Result(id) == "reply:hello", "Reply missing");
            await probe.Request("/api/command", new { id, text = "changed payload" });
            Require(await probe.Result(id) == "reply:hello" && calls == count + 1, "Duplicate executed");
            var saved = File.ReadAllText(Path.Combine(folder, "receipts.json"));
            Require(saved.Contains(id) && !saved.Contains(probe.Token), "Receipt missing or bearer token persisted");
        });
        await Check("Free phone handles stop while busy and isolates another phone's result", async () =>
        {
            var id = Guid.NewGuid().ToString("N"); await probe.Request("/api/command", new { id, text = "wait" }); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var count = calls; var duplicate = await probe.Request("/api/command", new { id, text = "wait" });
            Require(duplicate.Data.GetProperty("pending").GetBoolean(), "In-flight duplicate lost pending state");
            using var other = new PhoneProbe(server.Url); await other.Pair(server.Code);
            Require((await other.Request("/api/result?id=" + id)).Status == HttpStatusCode.NotFound, "Another phone read result");
            Require((await other.Command("another request")).Contains("다른 요청") && calls == count, "Busy command executed");
            await other.Request("/api/stop", new { });
            Require((await probe.Request("/api/result?id=" + id)).Data.GetProperty("pending").GetBoolean(), "Other phone cancelled request");
            await probe.Request("/api/stop", new { }); Require((await probe.Result(id)).Contains("중지"), "Stop failed");
        });
        await Check("Free phone revokes a logged-out token immediately", async () =>
        {
            await probe.Request("/api/logout", new { });
            Require((await probe.Request("/api/status")).Status == HttpStatusCode.Unauthorized, "Revoked token accepted");
        });
        await Check("Free phone limits pairing attempts", async () =>
        {
            HttpStatusCode last = 0;
            for (int i = 0; i < 11; i++) last = (await probe.Request("/api/pair", new { code = "bad" })).Status;
            Require(last == HttpStatusCode.TooManyRequests, "Pairing attempts unbounded");
        });
        await Check("Free phone shuts down its listener and restarts with new credentials", async () =>
        {
            var oldPort = new Uri(server.Url).Port;
            await server.DisposeAsync();
            Require(!Array.Exists(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), p => p.Port == oldPort && p.Address.Equals(IPAddress.Loopback)), "Listener survived stop");
            await using var replacement = new PhoneServer(Loopback, Path.Combine(folder, "receipts.json"), (_, _) => Task.FromResult("new"));
            await replacement.StartAsync(oldPort); using var next = new PhoneProbe(replacement.Url) { Token = probe.Token };
            Require((await next.Request("/api/status")).Status == HttpStatusCode.Unauthorized, "Previous token survived restart");
            await next.Pair(replacement.Code); Require(await next.Command("hello") == "new", "Restart failed");
        });
        return failed;
    }
}

public partial class App
{
    // Interactive, isolated loopback fixture for reviewing the actual packaged web UI.
    // Only the harmless clock route is enabled; it cannot expose the PC to another machine.
    async Task ShowPhonePreviewAsync()
    {
        if (string.Equals(Path.GetFullPath(AppPaths.Home).TrimEnd('\\'), Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { Shutdown(2); return; }
        settings = new UiSettings { Name = "폰시험", Speak = false, ProRemote = false };
        personal = new Personalization(Path.Combine(AppPaths.Home, "personal.json")); timers = new TimerStore(Path.Combine(AppPaths.Home, "timers.json"));
        window = new MainWindow(); overlay = new OverlayWindow(); MainWindow = window; window.LoadSettings(settings); overlay.Configure(settings.Desktop);
        phoneServer = new PhoneServer(PhoneChecks.Loopback, Path.Combine(AppPaths.Home, "phone-receipts.json"),
            (command, token) => command.Text == "지금 몇 시야" ? Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, token, true)).Task.Unwrap() : Task.FromResult("미리 보기에서는 ‘지금 몇 시야’만 실행합니다."), preview: true);
        await phoneServer.StartAsync(0);
        window.QuitRequested += Quit; window.Closing += OnWindowClosing; window.Show();
        phoneWindow = new PhoneWindow(_ => Task.CompletedTask, StopPhoneAsync, () => phoneServer) { Owner = window };
        phoneWindow.Show();
    }
    async Task<int> RunPhoneAppChecksAsync()
    {
        if (string.Equals(Path.GetFullPath(AppPaths.Home).TrimEnd('\\'), Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); int failed = 0;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        settings = new UiSettings { Name = "폰시험", Speak = false, ProRemote = false };
        personal = new Personalization(Path.Combine(AppPaths.Home, "personal.json")); timers = new TimerStore(Path.Combine(AppPaths.Home, "timers.json"));
        window = new MainWindow(); overlay = new OverlayWindow(); MainWindow = window; window.LoadSettings(settings); overlay.Configure(settings.Desktop);
        phoneServer = new PhoneServer(PhoneChecks.Loopback, Path.Combine(AppPaths.Home, "phone-receipts.json"),
            (command, token) => Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, token, true)).Task.Unwrap());
        try
        {
            await phoneServer.StartAsync(0); using var phone = new PhoneProbe(phoneServer.Url); await phone.Pair(phoneServer.Code);
            await Check("Phone WPF speech switch persists the shared reading preference without changing other voice options", () =>
            {
                var original = settings;
                var panel = new PhoneWindow(_ => Task.CompletedTask, () => Task.CompletedTask, () => phoneServer, speechEnabled: () => settings.Speak, setSpeech: SetPhoneSpeech);
                try
                {
                    Require(panel.ReadRepliesInput.IsChecked == false, "Initial speech state wrong");
                    panel.ReadRepliesInput.IsChecked = true;
                    var saved = SettingsStore.Load(); Require(settings.Speak && saved.Speak && saved.OutputId == original.OutputId && saved.SpeechVolume == original.SpeechVolume, "Speech choice not saved or other voice settings changed");
                    panel.ReadRepliesInput.IsChecked = false; Require(!settings.Speak && !SettingsStore.Load().Speak, "Reading cannot be turned off");
                }
                finally { panel.Close(); }
                return Task.CompletedTask;
            });
            await Check("Free phone HTTP reaches actual App clock without a Pro account or enabled Pro setting", async () =>
            { var result = await phone.Command("폰시험 지금 몇 시야"); Require(result.Contains("시") && result.Contains("답변 읽기가 꺼져") && !result.Contains("확인하지 못") && !busy && !settings.ProRemote, result); });
            await Check("Local paired phone identifies a normal App connection", async () =>
            { var status = (await phone.Request("/api/status")).Data; Require(status.GetProperty("client").GetProperty("kind").GetString() == "standard", "Incorrect client profile"); });
            await Check("Local phone creates lists and cancels actual App timers", () => VerifyPhoneTimersAsync(phone));
            await Check("Local phone reaches actual Windows speech playback with silent test volume", () => VerifyPhoneSpeechAsync(phone));
            await Check("Local phone receives a useful reason when PC speech output fails", () => VerifyPhoneSpeechAsync(phone, true));
            await Check("Free phone retains PC-review restrictions", async () =>
            { Require((await phone.Command("받아쓰기 시작")).Contains("PC에서") && !dictating && !busy, "PC review bypassed"); });
            await Check("Free phone refuses to interrupt ongoing PC work", async () =>
            { busy = true; try { Require((await phone.Command("지금 몇 시야")).Contains("다른 작업"), "Busy work interrupted"); } finally { busy = false; } });
            await Check("Free phone stop cancels actual queued App execution", async () =>
            {
                await executionGate.WaitAsync();
                try
                {
                    var id = Guid.NewGuid().ToString("N"); await phone.Request("/api/command", new { id, text = "지금 몇 시야" });
                    for (int i = 0; i < 100 && !busy; i++) await Task.Delay(20);
                    Require(busy, "App action never started"); await phone.Request("/api/stop", new { });
                    Require((await phone.Result(id)).Contains("중지") && !busy, "App action survived stop");
                }
                finally { executionGate.Release(); }
            });
            await Check("Free phone disconnect cancels actual App action and drains listener", async () =>
            {
                await executionGate.WaitAsync();
                try
                {
                    await phone.Request("/api/command", new { id = Guid.NewGuid().ToString("N"), text = "지금 몇 시야" });
                    for (int i = 0; i < 100 && !busy; i++) await Task.Delay(20);
                    Require(busy, "App action never started"); await StopPhoneAsync(); Require(!busy && phoneServer == null, "App action survived disconnect");
                }
                finally { executionGate.Release(); }
            });
        }
        finally { await StopPhoneAsync(); StopTurn(); overlay.Close(); window.Close(); }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "phone-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
