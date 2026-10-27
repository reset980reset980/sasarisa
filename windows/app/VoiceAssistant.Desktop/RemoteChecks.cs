using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class RemoteChecks
{
    const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    static readonly string Account = new('A', 64);
    static readonly string Other = new('B', 64);
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task<Exception> Fails(Func<Task> action) { try { await action(); } catch (Exception ex) { return ex; } throw new Exception("Expected failure"); }
    static async Task Until(Func<bool> ready)
    { using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6)); while (!ready()) await Task.Delay(15, limit.Token); }
    static string PathForTest() => Path.Combine(AppPaths.WindowsRoot, "artifacts", "remote-tests", Guid.NewGuid().ToString("N"), "receipts.json");
    static RemoteCommand Command(string id = "request-1", string text = "지금 몇 시야", string? account = null, CancellationToken lease = default) => new(id, text, account ?? Account, lease);
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    sealed class Transport : IRemoteTransport
    {
        public bool Linked => AccountIdentity.Length > 0;
        public string AccountIdentity { get; set; } = Account;
        public readonly Channel<RemoteCommand> Commands = Channel.CreateUnbounded<RemoteCommand>();
        public readonly ConcurrentQueue<RemoteReceipt> Replies = new();
        public int NextCalls, ReplyCalls, FailReplies;
        public async Task<RemoteCommand?> NextAsync(CancellationToken token)
        {
            Interlocked.Increment(ref NextCalls);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(100);
            try { return await Commands.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        }
        public Task<bool> ReplyAsync(RemoteReceipt receipt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref ReplyCalls);
            if (receipt.Account != AccountIdentity || Interlocked.Decrement(ref FailReplies) >= 0) return Task.FromResult(false);
            Replies.Enqueue(receipt); return Task.FromResult(true);
        }
        public void Send(RemoteCommand command) { if (!Commands.Writer.TryWrite(command)) throw new Exception("Queue closed"); }
    }
    public static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> run)
        { try { await run(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        await Check("Remote uses original HTTPS long-poll and reply contracts with bounded Unicode text", async () =>
        {
            int reads = 0, writes = 0;
            using var http = new HttpClient(new Handler(async (r, _) =>
            {
                Require(r.RequestUri!.Host == "maker.buffm.com" && r.RequestUri.Scheme == "https" && r.Headers.Authorization?.ToString() == "Bearer " + Key, "Remote destination/auth mismatch");
                if (r.Method == HttpMethod.Get)
                {
                    Require(r.RequestUri.AbsolutePath == "/api/butler/remote/next", "Poll path");
                    return ++reads == 1 ? Json(new { id = "phone-1", text = string.Concat(Enumerable.Repeat("👨‍👩‍👧", 301)) }) : new(HttpStatusCode.NoContent);
                }
                Require(r.Method == HttpMethod.Post && r.RequestUri.AbsolutePath == "/api/butler/remote/done", "Reply path");
                using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
                Require(body.RootElement.GetProperty("id").GetString() == "phone-1" && body.RootElement.GetProperty("result").GetString()!.Length == 400, "Reply limit"); writes++;
                return new(HttpStatusCode.NoContent);
            }));
            using var client = new ProAccountClient(http, () => Key, _ => { }, () => { });
            var command = await client.NextAsync(CancellationToken.None) ?? throw new Exception("Missing command");
            Require(System.Globalization.StringInfo.ParseCombiningCharacters(command.Text).Length == 300 && command.Account == client.AccountIdentity && command.Account != Key, "Command clip/identity");
            Require(await client.ReplyAsync(new(command.Id, command.Account, new string('가', 401), DateTimeOffset.UtcNow), CancellationToken.None), "204 acknowledgement");
            Require(await client.NextAsync(CancellationToken.None) == null, "204 should be empty");
            Require(!await client.ReplyAsync(new(command.Id, Other, "wrong account", DateTimeOffset.UtcNow), CancellationToken.None) && writes == 1, "Cross-account reply sent");
        });
        await Check("Remote authentication, membership backoff and malformed commands fail before execution", async () =>
        {
            foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.PaymentRequired })
            {
                string? key = Key; using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(code))));
                using var client = new ProAccountClient(http, () => key, x => key = x, () => key = null);
                var error = await Fails(() => client.NextAsync(CancellationToken.None));
                Require(error is RemoteUnavailable unavailable && (code != HttpStatusCode.PaymentRequired || unavailable.Retry == TimeSpan.FromHours(1)), "Remote error policy");
                Require(client.Linked == (code != HttpStatusCode.Unauthorized), "401 must invalidate credentials");
            }
            foreach (var value in new object[] { new { id = "", text = "command" }, new { id = "r", text = 12 }, new { id = "r", text = "bad\0text" }, new { id = "r", text = new string('x', 6001) } })
            {
                using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(value))));
                using var client = new ProAccountClient(http, () => Key, _ => { }, () => { });
                Require(await Fails(() => client.NextAsync(CancellationToken.None)) is InvalidOperationException, "Malformed command accepted");
            }
        });
        await Check("Remote unlink cancels a long poll and rejects a late response", async () =>
        {
            string? key = Key; var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); CancellationToken requestToken = default;
            using var http = new HttpClient(new Handler(async (r, t) =>
            {
                if (r.RequestUri!.AbsolutePath.EndsWith("unlink")) return new(HttpStatusCode.NoContent);
                requestToken = t; entered.SetResult(); await release.Task; return Json(new { id = "late", text = "지금 몇 시야" });
            }));
            using var client = new ProAccountClient(http, () => key, x => key = x, () => key = null);
            var pending = client.NextAsync(CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await client.UnlinkAsync(CancellationToken.None); Require(requestToken.IsCancellationRequested, "Poll did not cancel"); release.SetResult();
            Require(await Fails(() => pending) is OperationCanceledException && !client.Linked, "Late poll escaped unlink");
        });
        await Check("Remote claims survive process restart and isolate equal IDs across accounts", () =>
        {
            var path = PathForTest(); var ledger = new RemoteLedger(path); var first = ledger.Claim(Command()); Require(first.Fresh, "First claim");
            var restarted = new RemoteLedger(path); var repeated = restarted.Claim(Command());
            Require(!repeated.Fresh && repeated.Receipt.Result.Contains("확인하지 못"), "Crash claim must not rerun");
            Require(restarted.Claim(Command(account: Other)).Fresh, "Separate account ID collision");
            var done = restarted.Complete(repeated.Receipt, "완료 응답"); restarted.Acknowledge(done);
            Require(new RemoteLedger(path).Find(Account, "request-1") is { Acknowledged: true, Result: "완료 응답" }, "Durable completion");
            Require(!File.ReadAllText(path).Contains(Key), "Credential in ledger"); return Task.CompletedTask;
        });
        await Check("Remote corrupt records and failed claim writes never discard previous state", async () =>
        {
            var path = PathForTest(); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{broken");
            Require(await Fails(() => Task.FromResult(new RemoteLedger(path))) is InvalidOperationException, "Corrupt history reset");
            var blocked = PathForTest(); var ledger = new RemoteLedger(blocked); var directory = Path.GetDirectoryName(blocked)!;
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!); File.WriteAllText(directory, "blocks directory");
            await Fails(() => Task.FromResult(ledger.Claim(Command()))); Require(ledger.Pending.Count == 0, "Failed write mutated memory");
        });
        await Check("Remote duplicate delivery during execution and after completion runs only once", async () =>
        {
            var transport = new Transport(); var ledger = new RemoteLedger(PathForTest()); int executions = 0;
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var pump = new RemotePump(transport, ledger, async (_, t) => { Interlocked.Increment(ref executions); return await release.Task.WaitAsync(t); }, _ => { });
            var run = pump.RunAsync(stop.Token);
            try
            {
                transport.Send(Command()); await Until(() => executions == 1); transport.Send(Command()); await Until(() => transport.NextCalls >= 3);
                Require(transport.Replies.IsEmpty, "Running placeholder acknowledged"); release.SetResult("시각 응답");
                await Until(() => transport.Replies.Count == 1); transport.Send(Command()); await Until(() => transport.Replies.Count == 2);
                Require(executions == 1 && transport.Replies.All(r => r.Result == "시각 응답"), "Duplicate executed or stale result sent");
            }
            finally { stop.Cancel(); await run; }
        });
        await Check("Remote polling accepts busy and stop requests while a command is running", async () =>
        {
            var transport = new Transport(); int executions = 0; bool cancelled = false;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var pump = new RemotePump(transport, new RemoteLedger(PathForTest()), async (_, t) =>
            { Interlocked.Increment(ref executions); try { await Task.Delay(Timeout.Infinite, t); } finally { cancelled = t.IsCancellationRequested; } return "unexpected"; }, _ => { });
            var run = pump.RunAsync(stop.Token);
            try
            {
                transport.Send(Command()); await Until(() => executions == 1);
                transport.Send(Command("busy")); await Until(() => transport.Replies.Any(r => r.Id == "busy"));
                transport.Send(Command("stop", "그만")); await Until(() => cancelled && transport.Replies.Count >= 3);
                Require(executions == 1 && transport.Replies.Single(r => r.Id == "busy").Result.Contains("처리 중"), "Busy request replaced running work");
                Require(transport.Replies.Single(r => r.Id == "request-1").Result.Contains("중지"), "Stop result missing");
            }
            finally { stop.Cancel(); await run; }
        });
        await Check("Remote failed acknowledgements retry without rerunning and ignore old-account backlog", async () =>
        {
            var transport = new Transport { FailReplies = 1 }; var ledger = new RemoteLedger(PathForTest()); int executions = 0;
            for (int i = 0; i < 25; i++) ledger.Claim(Command("old-" + i, account: Other));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var pump = new RemotePump(transport, ledger, (_, _) => { executions++; return Task.FromResult("確定 결과"); }, _ => { }, retryInterval: TimeSpan.FromMilliseconds(1));
            var run = pump.RunAsync(stop.Token);
            try
            {
                transport.Send(Command()); await Until(() => transport.Replies.Count == 1);
                Require(executions == 1 && transport.ReplyCalls == 2 && transport.Replies.Single().Account == Account, "Retry duplicated execution or stale accounts starved current one");
                Require(ledger.Find(Account, "request-1")!.Acknowledged, "Retry not durable");
            }
            finally { stop.Cancel(); await run; }
        });
        await Check("Remote account leases and execution deadlines cancel actual workers", async () =>
        {
            foreach (bool expireLease in new[] { true, false })
            {
                var transport = new Transport(); int executions = 0; bool cancelled = false;
                using var lease = new CancellationTokenSource(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var pump = new RemotePump(transport, new RemoteLedger(PathForTest()), async (_, t) =>
                { executions++; try { await Task.Delay(Timeout.Infinite, t); } finally { cancelled = t.IsCancellationRequested; } return "unexpected"; }, _ => { }, requestLimit: TimeSpan.FromMilliseconds(150));
                var run = pump.RunAsync(stop.Token);
                try
                {
                    transport.Send(Command(lease: lease.Token)); await Until(() => executions == 1);
                    if (expireLease) { transport.AccountIdentity = Other; lease.Cancel(); }
                    await Until(() => cancelled); await Until(() => expireLease || transport.Replies.Count == 1);
                    Require(!transport.Replies.Any(r => r.Account != transport.AccountIdentity), "Response crossed account boundary");
                }
                finally { stop.Cancel(); await run; }
            }
        });
        await Check("Remote storage failure stops the pump before any command runs", async () =>
        {
            var path = PathForTest(); var ledger = new RemoteLedger(path); var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!); File.WriteAllText(directory, "blocked");
            var transport = new Transport(); int executions = 0; string status = "";
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var pump = new RemotePump(transport, ledger, (_, _) => { executions++; return Task.FromResult("bad"); }, x => status = x);
            transport.Send(Command()); await pump.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(3));
            Require(executions == 0 && status.Contains("중지"), "Storage failure left remote running");
        });
        await Check("Remote reply context survives awaits and excludes concurrent local UI messages", async () =>
        {
            var window = new MainWindow(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
            try
            {
                var context = new RemoteExecution(CancellationToken.None);
                var pending = context.RunAsync(async () => { window.AddMessage("user", "secret prompt"); entered.SetResult(); await release.Task; window.AddMessage("assistant", "휴대폰 응답"); });
                await entered.Task; Require(RemoteExecution.Current == null, "Remote context leaked to caller");
                window.AddMessage("assistant", "로컬 전용 응답"); release.SetResult();
                Require(await pending == "휴대폰 응답" && RemoteExecution.Current == null, "Mixed local/remote results");
            }
            finally { window.Close(); }
        });
        await Check("Remote default policy holds reviewed actions and imported settings never enable access", async () =>
        {
            foreach (var action in new[] { new AssistantAction("key", "enter"), new AssistantAction("kakao_send", "hello"), new AssistantAction("delegate_session"), new AssistantAction("screenshot"), new AssistantAction("dictation"), new AssistantAction("rewrite") })
            {
                var context = new RemoteExecution(CancellationToken.None);
                Require(await Fails(() => context.RunAsync(() => { RemoteExecution.Guard(action, true); return Task.CompletedTask; })) is AgentStoppedException, "Remote confirmation was bypassed");
            }
            await new RemoteExecution(CancellationToken.None).RunAsync(() => { RemoteExecution.Guard(new AssistantAction("now"), false); return Task.CompletedTask; });
            var imported = SettingsTransfer.Parse(SettingsTransfer.Serialize(new() { ProRemote = true }));
            Require(!SettingsTransfer.Prepare(imported, new(), true, true).ProRemote, "Import enabled remote");
            Require(SettingsTransfer.Prepare(new(), new() { ProRemote = true }, false, false).ProRemote, "Import changed existing explicit opt-in");
        });
        return failed;
    }
}
