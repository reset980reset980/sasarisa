using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

// All traffic is outbound HTTPS. A lost relay invalidates in-flight command leases.
public sealed class InternetPhone : IRemoteTransport, IAsyncDisposable
{
    public const string PublicUrl = "https://sasarisa.xsw.kr";
    readonly HttpClient http;
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> leases = new();
    string key = "";
    bool disposed;
    public string Url { get; }
    public string Code { get; private set; } = "";
    public DateTimeOffset PairUntil { get; private set; }
    public DateTimeOffset Expires { get; private set; }
    public int Connected { get; private set; }
    public string Status { get; set; } = "인터넷 연결을 준비하고 있어요.";
    public CancellationToken Token => lifetime.Token;
    public bool Linked => !disposed && key.Length > 0 && !lifetime.IsCancellationRequested;
    public string AccountIdentity { get; private set; } = "";
    public PhoneClientInfo Client { get; }
    public InternetPhone(string url = PublicUrl, bool preview = false)
    {
        Client = PhoneClientInfo.For(preview);
        var uri = new Uri(url);
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.Host == "127.0.0.1"))) throw new ArgumentException("An HTTPS relay origin is required");
        Url = uri.GetLeftPart(UriPartial.Authority);
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(35) };
    }
    async Task<JsonElement> Request(string path, object? body, CancellationToken token, bool close = false)
    {
        using var message = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, path);
        message.Headers.Add("X-Sasarisa-Client", "1");
        if (key.Length > 0) message.Headers.Authorization = new("Bearer", key);
        if (body != null) message.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !close)
        { Status = "인터넷 연결이 만료됐어요. 연결을 끄고 다시 켜세요."; lifetime.Cancel(); throw new OperationCanceledException(lifetime.Token); }
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { if (output.Length + count > 65536) throw new InvalidOperationException("Relay response is too large"); output.Write(buffer, 0, count); }
        using var json = JsonDocument.Parse(output.ToArray()); return json.RootElement.Clone();
    }
    public async Task OpenAsync(CancellationToken token = default)
    {
        if (key.Length > 0 || disposed) throw new InvalidOperationException("Already opened");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var data = await Request("/api/desktop/open", new { client = Client }, timeout.Token);
        key = data.GetProperty("token").GetString()!; Code = data.GetProperty("code").GetString()!;
        if (key.Length != 64 || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-f0-9]{64}$") || !System.Text.RegularExpressions.Regex.IsMatch(Code, "^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{12}$")) throw new InvalidOperationException("Invalid relay credentials");
        PairUntil = DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("pairUntil").GetInt64());
        Expires = DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("expires").GetInt64());
        var remaining = Expires - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromHours(9)) throw new InvalidOperationException("Invalid relay expiry");
        AccountIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        lifetime.CancelAfter(remaining); Status = "휴대폰에서 인터넷 주소와 연결 코드를 입력하세요.";
    }
    public async Task<RemoteCommand?> NextAsync(CancellationToken token)
    {
        using var joined = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        JsonElement data;
        try { data = await Request("/api/desktop/poll", null, joined.Token).ConfigureAwait(false); }
        catch { foreach (var lease in leases.Values) lease.Cancel(); throw; }
        Connected = data.GetProperty("connected").GetInt32();
        foreach (var item in data.GetProperty("cancelled").EnumerateArray())
        {
            var id = item.GetString()!;
            if (leases.TryGetValue(id, out var lease)) lease.Cancel();
            else if (Guid.TryParseExact(id, "N", out _)) await Request("/api/desktop/done", new { id, result = "실행 전에 요청을 취소했어요." }, joined.Token).ConfigureAwait(false);
        }
        var command = data.GetProperty("command"); if (command.ValueKind == JsonValueKind.Null) return null;
        var commandId = command.GetProperty("id").GetString()!; var text = command.GetProperty("text").GetString()!;
        if (!Guid.TryParseExact(commandId, "N", out _) || string.IsNullOrWhiteSpace(text) || text.Length > 1200) throw new InvalidOperationException("Invalid relay command");
        var own = leases.GetOrAdd(commandId, _ => CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
        return new(commandId, text, AccountIdentity, own.Token, Conversation(command, commandId));
    }
    internal static string Conversation(JsonElement command, string requestId)
    {
        // Older relays did not identify the phone: never resume another phone's
        // conversation merely because both are connected to the same PC room.
        if (!command.TryGetProperty("peer", out var value)) return "request:" + requestId;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 64 } peer || !System.Linq.Enumerable.All(peer, Uri.IsHexDigit))
            throw new InvalidOperationException("Invalid relay phone identity");
        return "peer:" + peer.ToUpperInvariant();
    }
    public async Task<bool> ReplyAsync(RemoteReceipt receipt, CancellationToken token)
    {
        if (!Linked || receipt.Account != AccountIdentity) return false;
        using var joined = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await Request("/api/desktop/done", new { id = receipt.Id, result = receipt.Result }, joined.Token).ConfigureAwait(false); return true;
    }
    public void Cancel() => lifetime.Cancel();
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel();
        foreach (var lease in leases.Values) lease.Cancel();
        if (key.Length > 0) try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await Request("/api/desktop/close", new { }, timeout.Token, true).ConfigureAwait(false); } catch (Exception) { }
        foreach (var lease in leases.Values) lease.Dispose(); leases.Clear(); http.Dispose(); lifetime.Dispose(); key = "";
    }
}
