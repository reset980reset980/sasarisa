using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record ProStatus(string Email, bool Active, bool Voice, string Plan, long Used, long Limit);
public sealed record ProLinkCode(string Code, Uri Page);

// Fixed service origin and profile-scoped Windows credentials. No device key is part of settings,
// backups, UI text, URLs or logs. Injected transport/storage are used by contract tests only.
public sealed partial class ProAccountClient : IDisposable, IRemoteTransport
{
    public const string Server = "https://maker.buffm.com";
    public static string CredentialTarget => "VoiceAssistantWindows:api:butler-pro:" + AppPaths.InstancePrefix;
    static readonly Lazy<ProAccountClient> shared = new(() => new());
    public static ProAccountClient Shared => shared.Value;
    public static readonly DeviceOption[] Voices =
    {
        new("CxErO97xpQgQXYmapDKX", "테오 · 한국어 남성"),
        new("QPFsEL6IBxlT15xfiD6C", "하나 · 한국어 여성"),
        new("qfwDoLSKOhzIgz7XedPT", "선 · 한국어 여성"),
        new("BuaKXS4Sv1Mccaw3flfU", "크리스티나 · 영어 여성"),
        new("nPczCjzI2devNBz1zQrb", "브라이언 · 영어 남성")
    };
    readonly object gate = new();
    readonly HttpClient http;
    readonly bool ownHttp;
    readonly Func<string?> read;
    readonly Action<string> save;
    readonly Action remove;
    readonly Func<DateTimeOffset> now;
    readonly TimeSpan pollInterval, linkTimeout, requestTimeout;
    CancellationTokenSource scope = new();
    CancellationTokenSource? linking;
    long revision;
    bool disposed, quarantine;
    ProStatus? status;
    DateTimeOffset voiceOffUntil;
    sealed class Session(string key, long revision, CancellationToken token)
    { public readonly string Key = key; public readonly long Revision = revision; public readonly CancellationToken Token = token; }

    public ProAccountClient(HttpClient? client = null, Func<string?>? readKey = null, Action<string>? saveKey = null,
        Action? deleteKey = null, Func<DateTimeOffset>? clock = null, TimeSpan? pollDelay = null,
        TimeSpan? linkLimit = null, TimeSpan? requestLimit = null)
    {
        ownHttp = client == null; http = client ?? ExternalVoice.CreateClient();
        read = readKey ?? (() => CredentialStore.Read(CredentialTarget));
        save = saveKey ?? (key => CredentialStore.Save(CredentialTarget, key));
        remove = deleteKey ?? (() => CredentialStore.Delete(CredentialTarget));
        now = clock ?? (() => DateTimeOffset.UtcNow);
        pollInterval = pollDelay ?? TimeSpan.FromSeconds(2); linkTimeout = linkLimit ?? TimeSpan.FromMinutes(10);
        requestTimeout = requestLimit ?? TimeSpan.FromSeconds(15);
    }
    public bool Linked { get { lock (gate) return !disposed && !quarantine && read() != null; } }
    public ProStatus? Status { get { lock (gate) return status; } }
    static bool ValidKey(string? key) => key is { Length: 64 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    void CheckAlive() { ObjectDisposedException.ThrowIf(disposed, this); }
    Session Current()
    {
        lock (gate)
        {
            CheckAlive(); var key = quarantine ? null : read();
            if (!ValidKey(key)) throw new InvalidOperationException("설정에서 Sasarisa Pro 계정을 먼저 연결하세요.");
            return new(key!, revision, scope.Token);
        }
    }
    bool Matches(Session session) => !disposed && !quarantine && session.Revision == revision && read() == session.Key;
    void EnsureCurrent(Session session, CancellationToken token)
    { token.ThrowIfCancellationRequested(); lock (gate) if (!Matches(session)) throw new OperationCanceledException("Pro 계정 연결이 변경됐어요."); }
    void Rotate()
    {
        revision++; status = null; voiceOffUntil = DateTimeOffset.MinValue;
        var old = scope; scope = new(); old.Cancel(); old.Dispose();
    }
    void Invalidate(Session session)
    {
        lock (gate)
        {
            if (!Matches(session)) return;
            quarantine = true; Rotate(); remove();
        }
    }
    static HttpRequestMessage Request(string path, HttpMethod method, object? body = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, Server + "/api/butler/" + path);
        if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }
    async Task<(HttpStatusCode Code, JsonElement Json)> JsonAsync(HttpRequestMessage request, CancellationToken token, bool readBody = true, TimeSpan? timeout = null)
    {
        using (request)
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            limit.CancelAfter(timeout ?? requestTimeout);
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK || !readBody) return (response.StatusCode, default);
                if (response.Content.Headers.ContentLength > 65536) throw new InvalidOperationException("Pro 응답이 너무 커서 처리하지 못했어요.");
                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                using var bytes = new System.IO.MemoryStream(); var chunk = new byte[4096];
                while (true)
                {
                    var count = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false); if (count == 0) break;
                    if (bytes.Length + count > 65536) throw new InvalidOperationException("Pro 응답이 너무 커서 처리하지 못했어요.");
                    bytes.Write(chunk, 0, count);
                }
                using var doc = JsonDocument.Parse(bytes.ToArray());
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                return (response.StatusCode, doc.RootElement.Clone());
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Pro 서버 응답 시간이 초과됐어요."); }
            catch (HttpRequestException) { throw new InvalidOperationException("Pro 서버에 연결하지 못했어요. 인터넷 연결을 확인하세요."); }
            catch (JsonException) { throw new InvalidOperationException("Pro 서버 응답을 확인하지 못했어요."); }
        }
    }
    static string Text(JsonElement value, string name, int max = 320) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { } text && text.Length <= max && !text.Any(char.IsControl) ? text : "";
    static bool Flag(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
    static long Count(JsonElement value, string name, long fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var count) && count >= 0 ? count : fallback;
    public static bool ApprovalPage(string url, out Uri? page)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out page) && page.Scheme == "https" && page.Host == "maker.buffm.com" &&
            page.Port == 443 && page.UserInfo.Length == 0 && page.AbsolutePath.TrimEnd('/') == "/butler/link" && page.Fragment.Length == 0;
    }
    public void CancelLink() { lock (gate) linking?.Cancel(); }
    public async Task LinkAsync(string name, Action<ProLinkCode> ready, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("호출 이름을 확인하세요.");
        using var deadline = new CancellationTokenSource(linkTimeout);
        CancellationTokenSource attempt; long started;
        lock (gate)
        {
            CheckAlive(); if (!quarantine && read() != null) throw new InvalidOperationException("이미 연결되어 있어요. 다른 계정을 연결하려면 먼저 해제하세요.");
            linking?.Cancel(); attempt = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token); linking = attempt; started = revision;
        }
        try
        {
            var start = await JsonAsync(Request("link/start", HttpMethod.Post, new { name, lang = "ko" }), attempt.Token).ConfigureAwait(false);
            var code = Text(start.Json, "code", 64); var poll = Text(start.Json, "poll", 2048); var url = Text(start.Json, "url", 4096);
            if (start.Code != HttpStatusCode.OK || code.Length == 0 || poll.Length == 0 || !ApprovalPage(url, out var page))
                throw new InvalidOperationException("Pro 연결 코드를 받지 못했어요. 잠시 후 다시 시도하세요.");
            attempt.Token.ThrowIfCancellationRequested(); ready(new(code, page!));
            while (true)
            {
                await Task.Delay(pollInterval, attempt.Token).ConfigureAwait(false);
                var response = await JsonAsync(Request("link/poll", HttpMethod.Post, new { poll }), attempt.Token).ConfigureAwait(false);
                if (response.Code == HttpStatusCode.NotFound || response.Code == HttpStatusCode.Gone) throw new InvalidOperationException("연결 코드가 만료됐어요. 새 코드를 받아 주세요.");
                if (response.Code != HttpStatusCode.OK && response.Code != HttpStatusCode.Accepted) throw new InvalidOperationException("계정 연결 확인에 실패했어요. 새 연결을 시작하세요.");
                if (Text(response.Json, "status") != "ok") continue;
                var key = Text(response.Json, "token", 64);
                if (!ValidKey(key)) throw new InvalidOperationException("Pro 연결 키를 확인하지 못했어요.");
                lock (gate)
                {
                    attempt.Token.ThrowIfCancellationRequested();
                    if (disposed || linking != attempt || revision != started) throw new OperationCanceledException();
                    save(key); quarantine = false; Rotate();
                }
                return;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Pro 계정 연결 시간이 초과됐어요."); }
        finally { lock (gate) if (linking == attempt) linking = null; attempt.Dispose(); }
    }
    public async Task<ProStatus> RefreshAsync(CancellationToken token)
    {
        var session = Current(); lock (gate) { EnsureCurrent(session, token); status = null; }
        using var joined = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var response = await JsonAsync(Request("me", HttpMethod.Get, key: session.Key), joined.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (response.Code == HttpStatusCode.Unauthorized) { Invalidate(session); throw new InvalidOperationException("계정 연결이 만료됐어요. 다시 연결하세요."); }
        if (response.Code != HttpStatusCode.OK) throw new InvalidOperationException("Pro 계정 상태를 확인하지 못했어요 (HTTP " + (int)response.Code + ").");
        var json = response.Json;
        json.TryGetProperty("pro", out var pro); json.TryGetProperty("voice", out var voice);
        var next = new ProStatus(Text(json, "email"), Flag(pro, "active"), Flag(pro, "voice"), Text(pro, "plan", 80), Count(voice, "used"), Count(voice, "limit", 25000));
        lock (gate) { EnsureCurrent(session, token); status = next; voiceOffUntil = DateTimeOffset.MinValue; }
        return next;
    }
    // Local use stops immediately. A failed revoke is reported separately, not presented as server success.
    public async Task<bool> UnlinkAsync(CancellationToken token)
    {
        string? key;
        lock (gate)
        {
            CheckAlive(); linking?.Cancel(); key = read(); quarantine = true; Rotate(); remove();
        }
        if (!ValidKey(key)) return true;
        try
        {
            var response = await JsonAsync(Request("unlink", HttpMethod.Post, new { }, key), token, readBody: false).ConfigureAwait(false);
            return response.Code is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.Unauthorized;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException or OperationCanceledException) { return false; }
    }
    public async Task<VoiceAudio> FetchVoiceAsync(string text, VoicePreferences options, CancellationToken token)
    {
        options.Validate(); token.ThrowIfCancellationRequested();
        if (options.Provider != "pro" || text.Length is < 1 or > 1500) throw new ArgumentException("Pro 음성으로 읽을 문장을 확인하세요.");
        var session = Current();
        lock (gate) if (now() < voiceOffUntil) throw new InvalidOperationException("Pro 음성 한도 또는 서버 상태로 잠시 기본 목소리를 사용해요. 계정 상태를 새로 확인하면 다시 시도합니다.");
        var common = new StringInfo(text).LengthInTextElements <= 40 && (text.EndsWith('?') || text.Contains("어서 오", StringComparison.Ordinal) || text.StartsWith("welcome", StringComparison.OrdinalIgnoreCase));
        var body = new System.Collections.Generic.Dictionary<string, object> { ["text"] = text, ["common"] = common };
        if (options.Voice.Length > 0) body["voice"] = options.Voice;
        using var request = Request("tts", HttpMethod.Post, body, session.Key);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token); limit.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            EnsureCurrent(session, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) Invalidate(session);
            if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.ServiceUnavailable)
                lock (gate) if (Matches(session)) voiceOffUntil = now().AddHours(1);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Pro 음성을 사용할 수 없어요 (HTTP " + (int)response.StatusCode + ").");
            var audio = await ExternalVoice.ReadAudioAsync(response, limit.Token).ConfigureAwait(false);
            EnsureCurrent(session, token); return audio;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException(session.Token.IsCancellationRequested ? "Pro 계정 연결이 변경됐어요." : "Pro 음성 생성 시간이 초과됐어요."); }
        catch (HttpRequestException) { throw new InvalidOperationException("Pro 음성 서버에 연결하지 못했어요."); }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; linking?.Cancel(); scope.Cancel(); scope.Dispose(); status = null; }
        if (ownHttp) http.Dispose();
    }
}
