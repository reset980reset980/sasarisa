using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record RemoteCommand(string Id, string Text, string Account, CancellationToken Lease, string? ConversationId = null);
public sealed record RemoteReceipt(string Id, string Account, string Result, DateTimeOffset Created, bool Acknowledged = false);
public sealed class RemoteUnavailable(string message, TimeSpan retry) : InvalidOperationException(message)
{ public TimeSpan Retry { get; } = retry; }
public interface IRemoteTransport
{
    bool Linked { get; }
    string AccountIdentity { get; }
    Task<RemoteCommand?> NextAsync(CancellationToken token);
    Task<bool> ReplyAsync(RemoteReceipt receipt, CancellationToken token);
}

public static class RemoteText
{
    public static string Clip(string text, int max)
    {
        var indices = StringInfo.ParseCombiningCharacters(text);
        return indices.Length <= max ? text : text[..indices[max]];
    }
    public static bool Stop(string text) => CommandParser.Parse(text)?.Actions.FirstOrDefault()?.Kind == "stop";
}

public sealed partial class ProAccountClient
{
    static string Identity(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    public string AccountIdentity { get { lock (gate) { var key = disposed || quarantine ? null : read(); return ValidKey(key) ? Identity(key!) : ""; } } }
    public async Task<RemoteCommand?> NextAsync(CancellationToken token)
    {
        var session = Current();
        using var joined = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var response = await JsonAsync(Request("remote/next", HttpMethod.Get, key: session.Key), joined.Token, timeout: TimeSpan.FromSeconds(40)).ConfigureAwait(false);
        EnsureCurrent(session, token);
        if (response.Code == HttpStatusCode.Unauthorized) { Invalidate(session); throw new RemoteUnavailable("계정을 다시 연결해야 휴대폰 명령을 받을 수 있어요.", TimeSpan.FromSeconds(2)); }
        if (response.Code == HttpStatusCode.PaymentRequired) throw new RemoteUnavailable("회원 상태를 확인하세요. 휴대폰 연결은 한 시간 뒤 다시 시도합니다.", TimeSpan.FromHours(1));
        if (response.Code == HttpStatusCode.NoContent) return null;
        if (response.Code != HttpStatusCode.OK) throw new RemoteUnavailable("휴대폰 연결을 다시 시도하고 있어요.", TimeSpan.FromSeconds(15));
        var id = Text(response.Json, "id", 200);
        if (!response.Json.TryGetProperty("text", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("휴대폰 명령 형식을 확인하지 못했어요.");
        var text = value.GetString() ?? ""; if (string.IsNullOrWhiteSpace(text)) return null;
        if (id.Length == 0 || text.Length > 6000 || text.Contains('\0')) throw new InvalidOperationException("휴대폰 명령 형식을 확인하지 못했어요.");
        return new(id, RemoteText.Clip(text.Trim(), 300), Identity(session.Key), session.Token);
    }
    public async Task<bool> ReplyAsync(RemoteReceipt receipt, CancellationToken token)
    {
        var session = Current();
        if (Identity(session.Key) != receipt.Account) return false;
        using var joined = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var response = await JsonAsync(Request("remote/done", HttpMethod.Post,
            new { id = receipt.Id, result = RemoteText.Clip(receipt.Result, 400) }, session.Key), joined.Token, readBody: false).ConfigureAwait(false);
        EnsureCurrent(session, token);
        if (response.Code == HttpStatusCode.Unauthorized) { Invalidate(session); return false; }
        return (int)response.Code is >= 200 and < 300;
    }
}

// Persist the claim BEFORE any action. A crash after an action cannot make a redelivery repeat it.
// Store only command IDs, non-reversible account fingerprints and short results; never device keys.
public sealed class RemoteLedger
{
    readonly object gate = new();
    readonly string file;
    List<RemoteReceipt> rows;
    public RemoteLedger(string path)
    {
        file = path;
        if (!File.Exists(file)) { rows = new(); return; }
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidOperationException("휴대폰 처리 기록이 너무 큽니다. 원격 연결을 중지했어요.");
        try { rows = JsonSerializer.Deserialize<List<RemoteReceipt>>(File.ReadAllText(file)) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("휴대폰 처리 기록을 읽지 못해 중복 실행 방지를 위해 원격 연결을 중지했어요."); }
        if (rows.Count > 5000 || rows.Any(r => r == null || r.Id is not { Length: > 0 and <= 200 } || r.Account is not { Length: 64 } || r.Result is not { Length: <= 4000 }) || rows.GroupBy(r => (r.Account, r.Id)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("휴대폰 처리 기록을 확인하지 못해 원격 연결을 중지했어요.");
    }
    public IReadOnlyList<RemoteReceipt> Pending { get { lock (gate) return rows.Where(x => !x.Acknowledged).ToArray(); } }
    public RemoteReceipt? Find(string account, string id) { lock (gate) return rows.Find(x => x.Account == account && x.Id == id); }
    void Save(List<RemoteReceipt> next)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file))!; Directory.CreateDirectory(directory);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(next)); output.Write(bytes); output.Flush(true); }
            if (File.Exists(file)) File.Replace(temp, file, file + ".previous"); else File.Move(temp, file);
            rows = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public (RemoteReceipt Receipt, bool Fresh) Claim(RemoteCommand command)
    {
        lock (gate)
        {
            var existing = rows.Find(x => x.Id == command.Id && x.Account == command.Account);
            if (existing != null) return (existing, false);
            // Do not evict IDs silently: an old server delivery must never become a new action.
            if (rows.Count >= 5000) throw new InvalidOperationException("휴대폰 처리 기록이 5,000건에 도달해 원격 실행을 중지했어요.");
            var row = new RemoteReceipt(command.Id, command.Account, "이 요청은 접수됐지만 완료 결과를 확인하지 못했어요. 중복 실행을 막기 위해 자동으로 다시 실행하지 않습니다.", DateTimeOffset.UtcNow);
            Save([.. rows, row]); return (row, true);
        }
    }
    public RemoteReceipt Complete(RemoteReceipt receipt, string result)
    {
        lock (gate)
        {
            var updated = receipt with { Result = RemoteText.Clip(string.IsNullOrWhiteSpace(result) ? "응답을 확인하지 못했어요." : result, 400), Acknowledged = false };
            Save(rows.Select(x => x.Id == receipt.Id && x.Account == receipt.Account ? updated : x).ToList()); return updated;
        }
    }
    public void Acknowledge(RemoteReceipt receipt)
    {
        lock (gate) Save(rows.Select(x => x.Id == receipt.Id && x.Account == receipt.Account && x.Result == receipt.Result ? x with { Acknowledged = true } : x).ToList());
    }
}

// One long poll, one executing command. Polling continues during execution so a phone stop works.
// Other commands get a busy result instead of silently replacing a task or building an unbounded queue.
public sealed class RemotePump(IRemoteTransport transport, RemoteLedger ledger,
    Func<RemoteCommand, CancellationToken, Task<string>> execute, Action<string> status,
    Func<string, bool>? isStop = null, TimeSpan? requestLimit = null, TimeSpan? retryInterval = null)
{
    readonly object gate = new();
    CancellationTokenSource? active;
    (string Account, string Id)? activeKey;
    readonly List<Task> work = new();
    public async Task RunAsync(CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var token = lifetime.Token;
        var nextRetry = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!transport.Linked) { status("계정을 연결하면 휴대폰 명령을 기다립니다."); await Task.Delay(2000, token); continue; }
                try
                {
                    if (DateTimeOffset.UtcNow >= nextRetry)
                    {
                        foreach (var receipt in ledger.Pending.Where(r => r.Account == transport.AccountIdentity).Take(20))
                        {
                            lock (gate) if ((receipt.Account, receipt.Id) == activeKey) continue;
                            await Publish(receipt, token);
                        }
                        nextRetry = DateTimeOffset.UtcNow.Add(retryInterval ?? TimeSpan.FromSeconds(20));
                    }
                    var command = await transport.NextAsync(token);
                    if (command == null) { lock (gate) if (active == null) status("휴대폰 명령을 기다리고 있어요."); await Task.Delay(500, token); continue; }
                    token.ThrowIfCancellationRequested(); command.Lease.ThrowIfCancellationRequested();
                    foreach (var completed in work.Where(t => t.IsCompleted).ToArray()) { await completed; work.Remove(completed); }
                    if (work.Count >= 24)
                    {
                        await Task.WhenAny(work).WaitAsync(token);
                        foreach (var completed in work.Where(t => t.IsCompleted).ToArray()) { await completed; work.Remove(completed); }
                    }
                    work.Add(Handle(command, lifetime));
                }
                catch (RemoteUnavailable ex) { status(ex.Message); await Task.Delay(ex.Retry, token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { status("계정 연결이 변경됐어요."); await Task.Delay(1000, token); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
                { status("휴대폰 연결을 다시 시도하고 있어요."); await Task.Delay(15000, token); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { status("휴대폰 처리 기록을 저장하지 못해 원격 연결을 중지했어요. 저장 공간과 권한을 확인하세요."); }
        finally
        {
            lifetime.Cancel();
            lock (gate) active?.Cancel();
            try { await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }
    }
    async Task Publish(RemoteReceipt receipt, CancellationToken token)
    {
        lock (gate)
        {
            if (activeKey == (receipt.Account, receipt.Id)) return;
            receipt = ledger.Find(receipt.Account, receipt.Id) ?? throw new InvalidOperationException("Missing remote receipt");
        }
        try { if (await transport.ReplyAsync(receipt, token)) ledger.Acknowledge(receipt); }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or TimeoutException or HttpRequestException) { }
    }
    async Task Handle(RemoteCommand command, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;
        RemoteReceipt receipt;
        try
        {
            token.ThrowIfCancellationRequested(); command.Lease.ThrowIfCancellationRequested();
            if (command.Account != transport.AccountIdentity) return;
            var claimed = ledger.Claim(command); receipt = claimed.Receipt;
            if (!claimed.Fresh)
            {
                lock (gate) if (activeKey == (receipt.Account, receipt.Id)) return;
                await Publish(receipt, token); return;
            }
            if ((isStop ?? RemoteText.Stop)(command.Text))
            {
                bool stopped; lock (gate) { stopped = active != null; active?.Cancel(); }
                receipt = ledger.Complete(receipt, stopped ? "진행 중인 휴대폰 요청에 중지를 전달했어요." : "진행 중인 휴대폰 요청이 없어요.");
                await Publish(receipt, token); return;
            }
            CancellationTokenSource? own = null;
            lock (gate)
            {
                if (active == null)
                {
                    own = CancellationTokenSource.CreateLinkedTokenSource(token, command.Lease);
                    own.CancelAfter(requestLimit ?? TimeSpan.FromMinutes(5)); active = own; activeKey = (command.Account, command.Id);
                }
            }
            if (own == null) { receipt = ledger.Complete(receipt, "다른 휴대폰 요청을 처리 중이에요. 끝난 뒤 다시 요청하거나 ‘중지’라고 보내 주세요."); await Publish(receipt, token); return; }
            try
            {
                string result;
                try { own.Token.ThrowIfCancellationRequested(); status("휴대폰 요청을 처리 중이에요."); result = await execute(command, own.Token); }
                catch (OperationCanceledException) { result = "요청을 중지했거나 처리 시간이 초과됐어요. 이미 실행된 동작은 화면에서 확인하세요."; }
                catch (Exception) { result = "요청을 마치지 못했어요. PC의 비서 화면에서 오류를 확인하세요."; }
                receipt = ledger.Complete(receipt, result);
            }
            finally { lock (gate) if (active == own) { active = null; activeKey = null; } own.Dispose(); }
            await Publish(receipt, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || command.Lease.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { status("휴대폰 처리 기록을 저장하지 못해 원격 연결을 중지했어요. 저장 공간과 권한을 확인하세요."); lifetime.Cancel(); }
    }
}
