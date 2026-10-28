using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    sealed class AsidePhoneFixture
    {
        internal readonly List<string[]> Calls = new();
        internal string Answer = "시험 페이지의 제목을 확인했어요.", Session = "phone_owned_001";
        internal bool Hold, StopFails, Fail;
        internal int Starts, Stops;
        internal Task<AsideProcessResult> Run(ProcessStartInfo info, Action<string>? line, CancellationToken token)
        {
            var args = info.ArgumentList.ToArray(); Calls.Add(args);
            if (args.SequenceEqual(new[] { "account", "status" })) return Task.FromResult(new AsideProcessResult(0, "* u0 fixture@example.invalid signed in", ""));
            if (args.Contains("stop")) { Stops++; return Task.FromResult(new AsideProcessResult(StopFails ? 1 : 0, "", "")); }
            Starts++; line?.Invoke("created new session: " + Session);
            if (Hold) return Wait();
            return Task.FromResult(new AsideProcessResult(Fail ? 7 : 0, Answer, Fail ? "private-page-content" : ""));
            async Task<AsideProcessResult> Wait() { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable"); }
        }
        internal AsideClient Client() => new(Run, findBinary: () => "aside.exe");
    }

    async Task<int> RunAsidePhoneAppChecksAsync(bool internet, bool native)
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); settings = settings with { BrainProvider = "aside" };
        var rows = new List<object>(); int failed = 0; var fixture = new AsidePhoneFixture(); brain.AsideTestClient = fixture.Client();
        void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); StopTurn(); await brain.WaitForAsideStopAsync(); }
            finally { fixture.Hold = fixture.StopFails = fixture.Fail = false; }
        }
        async Task Until(Func<bool> predicate)
        { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!predicate()) await Task.Delay(20, deadline.Token); }
        PhoneProbe? phone = null; string url = "", code = "";
        try
        {
            if (internet)
            {
                internetPhone = new InternetPhone(); await internetPhone.OpenAsync(); var connection = internetPhone;
                var pump = new RemotePump(connection, new RemoteLedger(Path.Combine(AppPaths.Home, "aside-internet-receipts.json")),
                    (command, ct) => Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, ct, true)).Task.Unwrap(), _ => { });
                internetPhoneTask = Task.Run(() => pump.RunAsync(connection.Token)); url = connection.Url; code = connection.Code;
            }
            else
            {
                phoneServer = new PhoneServer(PhoneChecks.Loopback, Path.Combine(AppPaths.Home, "aside-phone-receipts.json"),
                    (command, ct) => Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, ct, true)).Task.Unwrap());
                await phoneServer.StartAsync(0); url = phoneServer.Url; code = phoneServer.Code;
            }
            phone = new PhoneProbe(url); await phone.Pair(code);
            const string task = "시험 페이지 제목을 Aside로 확인해 줘";
            await Check("Paired phone reaches real App Aside path and returns final answer plus voice-off notice", async () =>
            {
                var result = await phone.Command(task);
                Require(result.Contains(fixture.Answer) && result.Contains("답변 읽기가 꺼져") && fixture.Starts == 1 && !busy, result);
                var args = fixture.Calls.Last(); Require(args.Take(4).SequenceEqual(new[] { "--permission", "guard", "--host", "local" }) && args[^1].Contains("PC의 Aside"), "Remote permissions or local host missing");
            });
            await Check("Duplicate phone delivery never executes Aside twice", async () =>
            {
                var id = Guid.NewGuid().ToString("N"); await phone.Request("/api/command", new { id, text = task }); var result = await phone.Result(id); var count = fixture.Starts;
                await phone.Request("/api/command", new { id, text = "다른 Aside 요청" }); Require(await phone.Result(id) == result && fixture.Starts == count, "Aside task redelivered");
            });
            await Check("Same phone continues its session; another phone starts a separate session", async () =>
            {
                await phone.Command(task); Require(fixture.Calls.Last().Contains("resume"), "Phone lost same-source continuation");
                using var other = new PhoneProbe(url); await other.Pair(code); await other.Command(task); Require(fixture.Calls.Last().Contains("exec"), "Another phone reused a session");
            });
            await Check("PC request and Pro remote cannot resume a paired phone's Aside conversation", async () =>
            {
                await SubmitAsync(task, false); Require(fixture.Calls.Last().Contains("exec"), "PC resumed phone conversation");
                settings.ProRemote = true;
                try { await RunRemoteCommandAsync(new(Guid.NewGuid().ToString("N"), task, new string('A', 64), default), default); Require(fixture.Calls.Last().Contains("exec"), "Pro resumed local conversation"); }
                finally { settings.ProRemote = false; }
            });
            await Check("Phone stop cancels Aside and reports acknowledged targeted session stop", async () =>
            {
                fixture.Hold = true; var before = fixture.Starts; var id = Guid.NewGuid().ToString("N"); await phone.Request("/api/command", new { id, text = task });
                await Until(() => fixture.Starts > before); await phone.Request("/api/stop", new { }); var result = await phone.Result(id);
                Require(result.Contains("Aside에 작업 중지를 전달") && fixture.Stops > 0 && !busy, result); fixture.Hold = false;
            });
            await Check("Failed Aside stop stays visible in the phone cancellation result", async () =>
            {
                fixture.Hold = fixture.StopFails = true; var before = fixture.Starts; var id = Guid.NewGuid().ToString("N"); await phone.Request("/api/command", new { id, text = task });
                await Until(() => fixture.Starts > before); await phone.Request("/api/stop", new { }); var result = await phone.Result(id);
                Require(result.Contains("중지는 확인하지 못") && !result.Contains("Aside에 작업 중지를 전달"), result);
                fixture.Hold = fixture.StopFails = false;
            });
            await Check("Local command preempts phone Aside without leaking its reply into phone result", async () =>
            {
                fixture.Hold = true; var before = fixture.Starts; var id = Guid.NewGuid().ToString("N"); await phone.Request("/api/command", new { id, text = task });
                await Until(() => fixture.Starts > before); await SubmitAsync("지금 몇 시야", false); var result = await phone.Result(id);
                Require(result.Contains("중지") && !result.Contains("현재 시각") && !busy, result); fixture.Hold = false;
            });
            await Check("CLI failure and missing CLI are returned to the phone without private output", async () =>
            {
                fixture.Fail = true; var result = await phone.Command(task); Require(result.Contains("완료되지") && !result.Contains("private-page-content") && !result.Contains(fixture.Answer), result); fixture.Fail = false;
                brain.AsideTestClient = new AsideClient(findBinary: () => null); result = await phone.Command(task); Require(result.Contains("CLI가 없어요"), result); brain.AsideTestClient = fixture.Client();
            });
            await Check("PC-review commands and busy local work remain blocked before Aside execution", async () =>
            {
                var before = fixture.Starts; Require((await phone.Command("받아쓰기 시작")).Contains("PC에서"), "Review restriction lost");
                busy = true; try { Require((await phone.Command(task)).Contains("다른 작업"), "Busy PC interrupted"); } finally { busy = false; }
                Require(fixture.Starts == before, "Blocked work reached CLI");
            });
            await Check("Aside final answer reaches actual PC speech playback with silent test volume", async () =>
            {
                var saved = settings; var log = Path.Combine(AppPaths.Home, "assistant.log"); var offset = File.Exists(log) ? File.ReadAllText(log).Length : 0;
                using var speechPhone = new PhoneProbe(url); await speechPhone.Pair(code);
                try
                {
                    settings = settings with { Speak = true, SpeechVolume = 0, SpeechRate = 5 };
                    var result = await speechPhone.Command(task); var added = File.ReadAllText(log)[offset..];
                    Require(result.Contains(fixture.Answer) && result.Contains("PC 답변 음량이 0") && added.Contains("[tts-audio] start provider=windows volume=0") && added.Contains("[tts-audio] done"), result);
                }
                finally { settings = saved; }
            });
            if (native) await Check("Actual Aside account preserves phone follow-up and isolates another phone over the relay", async () =>
            {
                brain.AsideTestClient = null;
                using var nativePhone = new PhoneProbe(url); await nativePhone.Pair(code);
                async Task<string> Ask(PhoneProbe client, string text)
                {
                    var id = Guid.NewGuid().ToString("N"); await client.Request("/api/command", new { id, text });
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                    while (true)
                    {
                        deadline.Token.ThrowIfCancellationRequested(); var row = await client.Request("/api/result?id=" + id);
                        if (!row.Data.GetProperty("pending").GetBoolean()) return row.Data.GetProperty("result").GetString()!;
                        await Task.Delay(200, deadline.Token);
                    }
                }
                var first = await Ask(nativePhone, "연결 시험입니다. 도구·브라우저·파일을 사용하거나 저장하지 말고 이 대화의 표식 KSD_PHONE_ASIDE_0236 만 그대로 답하세요.");
                Require(first.Contains("KSD_PHONE_ASIDE_0236") && !first.Contains("실패"), first);
                var follow = await Ask(nativePhone, "이 대화의 바로 앞 요청에서 받은 표식만 그대로 답하세요. 도구나 저장된 메모리를 검색하지 마세요.");
                Require(follow.Contains("KSD_PHONE_ASIDE_0236"), "Same-phone context lost: " + follow);
                var other = await Ask(phone, "연결 시험입니다. 도구·브라우저·파일·저장된 메모리를 사용하지 마세요. 이 대화에서 앞서 받은 표식이 없으면 NEW_PHONE 만 답하세요.");
                Require(other.Contains("NEW_PHONE") && !other.Contains("KSD_PHONE_ASIDE_0236"), "Cross-phone context: " + other);
                brain.AsideTestClient = fixture.Client();
            });
            await Check("Phone disconnect cancels its in-flight Aside and drains owned work", async () =>
            {
                brain.AsideTestClient = fixture.Client(); fixture.Hold = true; var before = fixture.Starts;
                await phone.Request("/api/command", new { id = Guid.NewGuid().ToString("N"), text = task }); await Until(() => fixture.Starts > before);
                var stopped = fixture.Stops; await StopPhoneAsync(); Require(!busy && fixture.Stops > stopped && await AsideClient.DrainAsync(), "Aside survived phone disconnect");
            });
        }
        catch (Exception ex) { failed++; rows.Add(new { name = "Aside phone fixture lifecycle", pass = false, error = ex.ToString() }); }
        finally { phone?.Dispose(); await StopPhoneAsync(); StopTurn(); await brain.WaitForAsideStopAsync(); overlay.Close(); window.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", internet ? "aside-internet-app-checks.json" : "aside-phone-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
