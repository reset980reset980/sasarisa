using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class AsideChecks
{
    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    sealed class Fixture
    {
        public readonly List<string[]> Calls = new();
        public string Account = "u0", Output = "첫 문단\n\n완료했어요.", Error = "";
        public int Exit;
        public bool CancelRun, StopFails;
        public Task<AsideProcessResult> Run(ProcessStartInfo info, Action<string>? line, CancellationToken token)
        {
            var args = info.ArgumentList.ToArray(); Calls.Add(args);
            Require(!info.UseShellExecute && info.CreateNoWindow && info.RedirectStandardInput, "Shell/interactive CLI was enabled");
            if (args.SequenceEqual(new[] { "account", "status" })) return Task.FromResult(new AsideProcessResult(0, $"* {Account}  fixture@example.invalid  signed in  profiles: Profile 0", ""));
            if (args.SequenceEqual(new[] { "--version" })) return Task.FromResult(new AsideProcessResult(0, "1.26.1023.1115", ""));
            if (args.Contains("stop")) return Task.FromResult(new AsideProcessResult(StopFails ? 1 : 0, "Stopped", ""));
            if (!args.Contains("resume")) line?.Invoke("\u001b[2mcreated new session: fixture_123\u001b[0m");
            if (CancelRun) return Wait();
            return Task.FromResult(new AsideProcessResult(Exit, "created new session: fixture_123\n" + Output, Error));
            async Task<AsideProcessResult> Wait() { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable"); }
        }
        public IEnumerable<string[]> Tasks => Calls.Where(a => a.Contains("exec") || a.Contains("resume"));
    }
    public static async Task<int> RunAsync(List<object> rows, bool native = false)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test)
        { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        await Check("Aside settings, provider names and serialization preserve selection", () =>
        {
            var value = new UiSettings { BrainProvider = "aside", BrainModel = "provider/model" }; SettingsStore.Validate(value);
            var copy = JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(value))!;
            Require(copy.BrainProvider == "aside" && Brain.NameFor("aside") == "Aside" && SetupReadiness.BrainStatus(copy).Contains("Aside"), "Provider fell back to Claude");
            return Task.CompletedTask;
        });
        await Check("Aside strips terminal controls and accepts only CLI session metadata", () =>
        {
            Require(AsideClient.CreatedSession("\u001b[2mcreated new session: l25F7E4bCJe04tFR\u001b[0m") == "l25F7E4bCJe04tFR", "Real CLI metadata not parsed");
            Require(AsideClient.CreatedSession("페이지: created new session: badbad") == null && AsideClient.CreatedSession("created new session: x; rm") == null, "Unrelated text became a session");
            Require(AsideClient.Answer("created new session: fixture_123\n중간 단계\n\n\u001b[32m완료\u001b[0m") == "완료", "Progress or escape sequence read as final answer");
            Require(AsideClient.Answer("created new session: fixture_123\n") == "" && AsideClient.Clean("\u001b]0;private title\u0007본문") == "본문", "Metadata was accepted as answer");
            Require(AsideClient.Answer("첫 줄\n\n둘째 줄", true) == "첫 줄\n\n둘째 줄", "Dictation paragraphs lost");
            Require(!AsideClient.SignedIn("* u0  a@example.invalid  not signed in\n  u1  b@example.invalid  signed in"), "Another account made active signed-out account ready");
            return Task.CompletedTask;
        });
        await Check("Aside actual Windows CLI trace boundary excludes thinking and browser snapshots", () =>
        {
            var trace = "\u001b[2mThinking: internal progress\n\nmore progress\u001b[0m\n\n\u001b[32mrepl\u001b[0m(title: 'test')\n\n\u001b[2m > ✔︎ Opened a new tab\n- title: Example Domain\n- text: private page content\u001b[0m\n최상단 제목은 Example Domain입니다.\u001b[0m";
            Require(AsideClient.Answer(trace) == "최상단 제목은 Example Domain입니다.", "Tool trace leaked into spoken answer");
            Require(AsideClient.Answer("\u001b[2mThinking: incomplete") == "" && AsideClient.Answer("\u001b[2m > ✔︎ Opened\u001b[0m") == "", "Progress-only output accepted as completion");
            Require(AsideClient.Answer("> ✔︎ Opened a new tab\n- title: Example Domain\nunclear response") == "", "Unframed plain tool output accepted");
            Require(AsideClient.Answer("\u001b[2mThinking: progress\u001b[0m\n첫 문단\n\n둘째 문단", true) == "첫 문단\n\n둘째 문단", "Text polishing kept progress or lost paragraphs");
            return Task.CompletedTask;
        });
        await Check("Aside task uses argument boundaries, Guard, pinned account and current resume syntax", async () =>
        {
            var f = new Fixture(); var c = new AsideClient(f.Run, findBinary: () => "aside.exe");
            const string request = "한국어 \"따옴표\" & $(do-not-run)\n두 번째 줄";
            Require(await c.AskAsync(request, "참고", "p/model", CancellationToken.None) == "완료했어요.", "Answer missing");
            await c.AskAsync("이어서", "", "p/model", CancellationToken.None);
            var calls = f.Tasks.ToArray(); var first = calls[0]; var second = calls[1];
            Require(first.Take(6).SequenceEqual(new[] { "--permission", "guard", "--account", "u0", "--model", "p/model" }) && first[^2] == "--", "Permission, account, model or prompt boundary missing");
            Require(!first.Contains("full-access") && second.Contains("resume") && second.Contains("fixture_123") && !second.Contains("--session"), "Unsafe mode or obsolete continuation used");
            var json = first[^1][first[^1].IndexOf('{')..]; using var parsed = JsonDocument.Parse(json);
            Require(parsed.RootElement.GetProperty("user_request").GetString() == request, "Request changed by quoting");
        });
        await Check("Aside expires sessions and isolates model, account and settings changes", async () =>
        {
            var f = new Fixture(); var time = DateTimeOffset.UtcNow; var c = new AsideClient(f.Run, () => time, () => "aside.exe");
            await c.AskAsync("one", "", "", CancellationToken.None); time += TimeSpan.FromMinutes(10);
            await c.AskAsync("two", "", "", CancellationToken.None);
            await c.AskAsync("three", "", "different", CancellationToken.None); f.Account = "u1";
            await c.AskAsync("four", "", "different", CancellationToken.None); c.ResetSession();
            await c.AskAsync("five", "", "different", CancellationToken.None);
            Require(f.Tasks.All(a => a.Contains("exec")), "Expired or cross-account conversation reused");
        });
        await Check("Aside remote conversations isolate PC and paired phones and pin execution to this PC", async () =>
        {
            var f = new Fixture(); var c = new AsideClient(f.Run, findBinary: () => "aside.exe");
            await c.AskAsync("phone first", "", "", default, conversationScope: "remote:phone-A", remote: true);
            await c.AskAsync("phone follow-up", "", "", default, conversationScope: "remote:phone-A", remote: true);
            await c.AskAsync("other phone", "", "", default, conversationScope: "remote:phone-B", remote: true);
            await c.AskAsync("local", "", "", default);
            await c.AskAsync("back to phone", "", "", default, conversationScope: "remote:phone-A", remote: true);
            var calls = f.Tasks.ToArray();
            Require(calls[1].Contains("resume") && new[] { 0, 2, 3, 4 }.All(i => calls[i].Contains("exec")), "Conversation crossed a source boundary");
            Require(new[] { 0, 1, 2, 4 }.All(i => calls[i].Take(4).SequenceEqual(new[] { "--permission", "guard", "--host", "local" }) && calls[i][^1].Contains("휴대폰")), "Remote host or permission/prompt boundary missing");
        });
        await Check("Aside remote cancellation targets the local host and only its captured session", async () =>
        {
            var f = new Fixture { CancelRun = true }; var c = new AsideClient(f.Run, findBinary: () => "aside.exe");
            using var stop = new CancellationTokenSource();
            try { await c.AskAsync("phone", "", "", stop.Token, p => { if (p.Contains("세션")) stop.Cancel(); }, conversationScope: "remote:A", remote: true); throw new Exception("Ignored cancellation"); }
            catch (OperationCanceledException) { }
            Require(f.Calls.Last().SequenceEqual(new[] { "--host", "local", "--account", "u0", "session", "stop", "fixture_123" }), "Stopped another host or session");
        });
        await Check("Internet phone identity rejects malformed peers and isolates legacy requests", () =>
        {
            using var legacy = JsonDocument.Parse("{}");
            Require(InternetPhone.Conversation(legacy.RootElement, "one") != InternetPhone.Conversation(legacy.RootElement, "two"), "Legacy relay joined phone conversations");
            using var valid = JsonDocument.Parse(JsonSerializer.Serialize(new { peer = new string('a', 64) }));
            Require(InternetPhone.Conversation(valid.RootElement, "one") == InternetPhone.Conversation(valid.RootElement, "two"), "Stable peer was not preserved");
            foreach (var json in new[] { "{\"peer\":null}", "{\"peer\":7}", "{\"peer\":\"forged\"}" })
            {
                using var invalid = JsonDocument.Parse(json);
                try { InternetPhone.Conversation(invalid.RootElement, "one"); throw new Exception("Malformed peer accepted"); } catch (InvalidOperationException) { }
            }
            return Task.CompletedTask;
        });
        await Check("Aside nonzero exit never reports stdout as success and clears continuation", async () =>
        {
            var f = new Fixture { Exit = 7, Error = "Unauthorized private-token-value", Output = "완료했어요" }; var c = new AsideClient(f.Run, findBinary: () => "aside.exe");
            try { await c.AskAsync("test", "", "", CancellationToken.None); throw new Exception("Nonzero accepted"); }
            catch (InvalidOperationException ex) { Require(ex.Message.Contains("로그인") && !ex.Message.Contains("private-token"), "Auth hint missing or private error echoed"); }
            Require(f.Calls.Any(a => a.Contains("stop")), "Failed session not stopped"); f.Exit = 0; f.Error = "";
            await c.AskAsync("next", "", "", CancellationToken.None); Require(f.Tasks.Last().Contains("exec"), "Failed session reused");
        });
        await Check("Aside cancellation stops exactly its session with a fresh token and drains on exit", async () =>
        {
            var f = new Fixture { CancelRun = true }; var c = new AsideClient(f.Run, findBinary: () => "aside.exe");
            using var cancel = new CancellationTokenSource(); var notices = new List<string>();
            var task = c.AskAsync("test", "", "", cancel.Token, value => { if (value.Contains("세션")) cancel.Cancel(); }, notices.Add);
            try { await task; throw new Exception("Cancellation swallowed"); } catch (OperationCanceledException) { }
            Require(f.Calls.Last().SequenceEqual(new[] { "--account", "u0", "session", "stop", "fixture_123" }) && notices.Single().Contains("전달"), "Wrong session stopped or stop skipped");
            Require(await c.WaitForIdleAsync() && await AsideClient.DrainAsync(), "Exit wait did not drain");
        });
        await Check("Aside timeout and rejected stop remain visible; no false stopped claim", async () =>
        {
            var f = new Fixture { CancelRun = true, StopFails = true }; var c = new AsideClient(f.Run, findBinary: () => "aside.exe"); var notices = new List<string>();
            try { await c.AskAsync("test", "", "", CancellationToken.None, notice: notices.Add, timeout: TimeSpan.FromMilliseconds(30)); throw new Exception("Timeout ignored"); }
            catch (TimeoutException) { }
            Require(notices.Single().Contains("확인하지 못했어요"), "Failed remote stop called successful");
        });
        await Check("Aside readiness creates no task and does not expose email", async () =>
        {
            var f = new Fixture(); var value = await new AsideClient(f.Run, findBinary: () => "aside.exe").CheckAsync(CancellationToken.None);
            Require(value.SignedIn && value.Account == "u0" && value.Version == "1.26.1023.1115" && !value.Summary.Contains("@") && !f.Tasks.Any(), "Readiness launched a task or exposed account details");
        });
        await Check("Aside native process reads Unicode stdout/stderr, exit code and cancellation", async () =>
        {
            var exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            ProcessStartInfo Script(string value) => AsideClient.Info(exe, new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + value)) });
            var diagnostics = new List<string>();
            var r = await AsideClient.RunProcessAsync(Script("[Console]::Out.WriteLine('한국어 출력'); [Console]::Out.WriteLine('created new session: untrusted_123'); [Console]::Error.WriteLine('오류 내용'); [Console]::Error.WriteLine('created new session: actual_123'); exit 7"), diagnostics.Add, CancellationToken.None);
            Require(r.ExitCode == 7 && r.Output.Contains("한국어 출력") && r.Error.Contains("오류 내용"), "Native pipes/exit lost");
            Require(diagnostics.Any(x => x.Contains("actual_123")) && diagnostics.All(x => !x.Contains("untrusted_123")), "Model/page stdout selected a session");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8)); int processId = 0;
            try { await AsideClient.RunProcessAsync(Script("[Console]::Error.WriteLine($PID); Start-Sleep -Seconds 30"), line => { if (int.TryParse(line, out processId)) cancel.Cancel(); }, cancel.Token); throw new Exception("Process cancellation ignored"); }
            catch (OperationCanceledException) { }
            Require(processId != 0, "Canceled without process becoming ready");
            try { using var p = Process.GetProcessById(processId); Require(p.HasExited, "CLI survived cancellation"); } catch (ArgumentException) { }
        });
        if (native)
        {
            await Check("Aside installed CLI account and two real turns preserve the same session", async () =>
            {
                var c = new AsideClient(); var connection = await c.CheckAsync(CancellationToken.None);
                Require(connection.SignedIn, "Aside account is not signed in");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                await c.AskAsync("연결 시험입니다. 표식 코드는 KSD_ASIDE_5279 입니다. 어떤 도구도 쓰지 말고 기억했다고만 답하세요.", "", "", deadline.Token);
                var answer = await c.AskAsync("방금 전달한 표식만 그대로 답하세요. 도구는 쓰지 마세요.", "", "", deadline.Token);
                Require(answer.Contains("KSD_ASIDE_5279"), "Actual follow-up did not preserve context: " + answer);
            });
            await Check("Aside actual running task accepts targeted stop on cancellation", async () =>
            {
                var c = new AsideClient(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var notices = new List<string>();
                try
                {
                    await c.AskAsync("중지 시험입니다. 브라우저나 파일 등 어떤 도구도 쓰지 말고 1부터 3000까지의 수를 한 줄씩 적으세요.", "", "", cancel.Token,
                        value => { if (value.Contains("세션")) cancel.CancelAfter(500); }, notices.Add);
                    throw new Exception("Task finished before cancellation could be verified");
                }
                catch (OperationCanceledException) { }
                Require(notices.Any(x => x.Contains("중지를 전달")) && notices.All(x => !x.Contains("확인하지 못")), "Real session stop was not acknowledged");
            });
        }
        return failed;
    }
}
