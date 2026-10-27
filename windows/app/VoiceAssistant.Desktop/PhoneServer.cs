using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.Desktop;

public sealed record PhoneNetwork(string Name, IPAddress Address, IPAddress Mask)
{
    public string Label => $"{Name} · {Address}";
    public bool Contains(IPAddress? peer)
    {
        if (peer == null || peer.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(Address)) return IPAddress.IsLoopback(peer);
        if (!Private(peer)) return false;
        var a = Address.GetAddressBytes(); var p = peer.GetAddressBytes(); var m = Mask.GetAddressBytes();
        return Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (p[i] & m[i]));
    }
    static bool Private(IPAddress address)
    {
        var a = address.GetAddressBytes();
        return a.Length == 4 && (a[0] == 10 || (a[0] == 172 && a[1] >= 16 && a[1] <= 31) || (a[0] == 192 && a[1] == 168));
    }
    public static PhoneNetwork[] Available() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
        .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => Private(a.Address)).Select(a => new PhoneNetwork(n.Name, a.Address, a.IPv4Mask)))
        .ToArray();
}

// A session-only server, independent of the original Pro service. It binds one selected
// private interface and accepts peers on that subnet. No cookies, external assets or CORS.
public sealed class PhoneServer : IAsyncDisposable
{
    sealed class Session(string token)
    {
        public string Token { get; } = token;
        public string Account { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddHours(8);
        public DateTimeOffset Minute = DateTimeOffset.UtcNow;
        public int Requests;
    }
    readonly object gate = new();
    readonly PhoneNetwork network;
    readonly RemoteLedger ledger;
    readonly Func<RemoteCommand, CancellationToken, Task<string>> execute;
    readonly Func<string, bool> isStop;
    readonly CancellationTokenSource lifetime = new();
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    WebApplication? host;
    CancellationTokenSource? activeStop;
    (string Account, string Id)? activeKey;
    Task work = Task.CompletedTask;
    bool stopped, storageFailed;
    DateTimeOffset pairMinute = DateTimeOffset.UtcNow;
    int pairAttempts;
    public string Code { get; } = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
    public DateTimeOffset PairUntil { get; } = DateTimeOffset.UtcNow.AddMinutes(10);
    public string Url { get; private set; } = "";
    public PhoneNetwork Network => network;
    public string? Problem { get { lock (gate) return storageFailed ? "처리 기록을 저장하지 못해 명령 수신을 중지했어요. PC 저장 공간을 확인한 뒤 연결을 다시 켜세요." : null; } }
    public int Connected { get { lock (gate) return sessions.Values.Count(s => s.Expires > DateTimeOffset.UtcNow); } }
    public PhoneServer(PhoneNetwork network, string ledgerFile, Func<RemoteCommand, CancellationToken, Task<string>> execute, Func<string, bool>? isStop = null)
    { this.network = network; ledger = new RemoteLedger(ledgerFile); this.execute = execute; this.isStop = isStop ?? RemoteText.Stop; }

    public async Task StartAsync(int port = 17845)
    {
        if (host != null || stopped) throw new InvalidOperationException("Already started");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear(); // Environment/working-directory configuration cannot widen the binding.
        builder.Logging.ClearProviders(); // Never log pairing codes, bearer tokens or command bodies.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 4096;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            options.Listen(network.Address, port, endpoint => endpoint.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
        });
        host = builder.Build(); host.Run(HandleAsync);
        try
        {
            await host.StartAsync(lifetime.Token).ConfigureAwait(false);
            Url = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
        }
        catch { await host.DisposeAsync().ConfigureAwait(false); host = null; throw; }
    }
    async Task HandleAsync(HttpContext context)
    {
        var request = context.Request; var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        try
        {
            var authority = $"{network.Address}:{context.Connection.LocalPort}";
            if (!network.Contains(context.Connection.RemoteIpAddress) || request.Host.Value != authority || stopped)
            { await Error(context, 403, "이 PC의 로컬 주소로 다시 연결하세요."); return; }
            if (request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != "http://" + authority)
            { await Error(context, 403, "다른 사이트에서 보낸 요청은 받지 않습니다."); return; }
            if (request.Method == "GET" && request.Path == "/api/info")
            { await response.WriteAsJsonAsync(new { mode = "local", voice = false }); return; }
            if (request.Method == "GET" && request.Path == "/source.zip")
            {
                var source = Path.Combine(AppPaths.WindowsRoot, "Sasarisa-Windows-Source.zip");
                if (!File.Exists(source)) { await Error(context, 404, "소스 압축 파일은 설치본에 포함됩니다. GitHub의 reset980reset980/sasarisa windows 브랜치에서도 확인할 수 있어요."); return; }
                response.ContentType = "application/zip"; response.Headers.ContentDisposition = "attachment; filename=Sasarisa-Windows-Source.zip";
                await response.SendFileAsync(source, context.RequestAborted); return;
            }
            if (request.Method == "GET" && request.Path.Value is "/" or "/phone.js" or "/phone.css")
            {
                var asset = request.Path == "/" ? "index.html" : request.Path.Value![1..];
                response.ContentType = asset.EndsWith(".js") ? "text/javascript; charset=utf-8" : asset.EndsWith(".css") ? "text/css; charset=utf-8" : "text/html; charset=utf-8";
                using var stream = typeof(PhoneServer).Assembly.GetManifestResourceStream("VoiceAssistant.Desktop.PhoneWeb." + asset)!;
                await stream.CopyToAsync(response.Body, context.RequestAborted); return;
            }
            if (request.Headers["X-Sasarisa-Client"] != "1" || (request.Method == "POST" && (!request.Headers.ContainsKey("Origin") || !request.HasJsonContentType())))
            { await Error(context, 403, "휴대폰 연결 화면에서 다시 요청하세요."); return; }
            if (request.Path == "/api/pair" && request.Method == "POST") { await PairAsync(context); return; }
            Session? session;
            lock (gate)
            {
                var auth = request.Headers.Authorization.ToString();
                sessions.TryGetValue(auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "", out session);
                if (session?.Expires <= DateTimeOffset.UtcNow) session = null;
            }
            if (session == null) { await Error(context, 401, "연결이 만료됐어요. PC의 코드로 다시 연결하세요."); return; }
            switch (request.Method, request.Path.Value)
            {
                case ("GET", "/api/status"):
                    await response.WriteAsJsonAsync(new { connected = true, expires = session.Expires }); return;
                case ("POST", "/api/command"):
                    await CommandAsync(context, session); return;
                case ("GET", "/api/result"):
                    object? result;
                    lock (gate)
                    {
                        var receipt = ledger.Find(session.Account, request.Query["id"].ToString());
                        result = receipt == null ? null : View(receipt);
                    }
                    if (result == null) await Error(context, 404, "요청 기록을 찾지 못했어요."); else await response.WriteAsJsonAsync(result);
                    return;
                case ("POST", "/api/stop"):
                    lock (gate) if (activeKey?.Account == session.Account) activeStop?.Cancel();
                    await response.WriteAsJsonAsync(new { message = "중지를 요청했어요. 이미 실행된 동작은 PC에서 확인하세요." }); return;
                case ("POST", "/api/logout"):
                    lock (gate) { sessions.Remove(session.Token); if (activeKey?.Account == session.Account) activeStop?.Cancel(); }
                    await response.WriteAsJsonAsync(new { disconnected = true }); return;
                default: await Error(context, 404, "없는 주소입니다."); return;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        { if (!response.HasStarted) await Error(context, 400, "입력 형식이나 길이를 확인하세요."); }
        catch (Exception ex)
        {
            AppPaths.Log("phone-server", ex.GetType().Name);
            if (!response.HasStarted) await Error(context, 503, "처리하지 못했어요. PC에서 휴대폰 연결 상태를 확인하세요.");
        }
    }
    static Task Error(HttpContext context, int code, string message)
    { context.Response.StatusCode = code; return context.Response.WriteAsJsonAsync(new { error = message }); }
    async Task PairAsync(HttpContext context)
    {
        bool allowed;
        lock (gate)
        {
            if (DateTimeOffset.UtcNow - pairMinute > TimeSpan.FromMinutes(1)) { pairMinute = DateTimeOffset.UtcNow; pairAttempts = 0; }
            allowed = ++pairAttempts <= 10;
        }
        if (!allowed) { await Error(context, 429, "연결 시도가 많아요. 1분 뒤 다시 시도하세요."); return; }
        using var json = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 4 }, context.RequestAborted);
        var code = String(json.RootElement, "code");
        if (DateTimeOffset.UtcNow > PairUntil || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(Code)))
        { await Error(context, 401, "코드가 다르거나 10분이 지났어요. PC에서 새 코드를 받으세요."); return; }
        Session? session = null;
        lock (gate)
        {
            foreach (var expired in sessions.Values.Where(s => s.Expires < DateTimeOffset.UtcNow).ToArray()) sessions.Remove(expired.Token);
            if (!stopped && sessions.Count < 4)
            {
                session = new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                sessions.Add(session.Token, session);
            }
        }
        if (session == null) { await Error(context, 409, "최대 4대까지 연결할 수 있어요. PC에서 연결을 초기화하세요."); return; }
        await context.Response.WriteAsJsonAsync(new { token = session.Token, expires = session.Expires });
    }
    static string String(JsonElement json, string key) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    object View(RemoteReceipt receipt) => new { id = receipt.Id, pending = activeKey == (receipt.Account, receipt.Id), result = activeKey == (receipt.Account, receipt.Id) ? "처리 중이에요…" : receipt.Result };
    async Task CommandAsync(HttpContext context, Session session)
    {
        using var json = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 4 }, context.RequestAborted);
        var id = String(json.RootElement, "id"); var text = String(json.RootElement, "text").Trim();
        if (!Guid.TryParseExact(id, "N", out _) || text.Length == 0 || text.Length > 1200 || new StringInfo(text).LengthInTextElements > 300 || text.Any(c => char.IsControl(c) && c != '\n' && c != '\t'))
        { await Error(context, 400, "명령은 1~300자로 입력하세요."); return; }
        object result;
        lock (gate)
        {
            if (stopped || storageFailed || !sessions.ContainsKey(session.Token) || session.Expires <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Session closed");
            var prior = ledger.Find(session.Account, id);
            if (prior != null) result = View(prior);
            else
            {
                if (DateTimeOffset.UtcNow - session.Minute > TimeSpan.FromMinutes(1)) { session.Minute = DateTimeOffset.UtcNow; session.Requests = 0; }
                if (++session.Requests > 12) { context.Response.StatusCode = 429; result = new { error = "명령이 너무 많아요. 1분 뒤 다시 보내세요." }; }
                else
                {
                    var command = new RemoteCommand(id, text, session.Account, lifetime.Token);
                    var receipt = ledger.Claim(command).Receipt; // durable BEFORE execution
                    if (isStop(text))
                    {
                        if (activeKey?.Account == session.Account) activeStop?.Cancel();
                        result = View(ledger.Complete(receipt, "중지를 요청했어요."));
                    }
                    else if (activeKey != null) result = View(ledger.Complete(receipt, "PC에서 다른 요청을 처리 중이에요. 끝난 뒤 다시 보내 주세요."));
                    else
                    {
                        activeStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        activeStop.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Math.Min(300, (session.Expires - DateTimeOffset.UtcNow).TotalSeconds))));
                        activeKey = (session.Account, id);
                        var stop = activeStop;
                        work = Task.Run(() => RunCommandAsync(command, receipt, stop));
                        result = View(receipt);
                    }
                }
            }
        }
        await context.Response.WriteAsJsonAsync(result);
    }
    async Task RunCommandAsync(RemoteCommand command, RemoteReceipt receipt, CancellationTokenSource stop)
    {
        string result;
        try { stop.Token.ThrowIfCancellationRequested(); result = await execute(command, stop.Token); }
        catch (OperationCanceledException) { result = "요청을 중지했거나 시간이 초과됐어요. 이미 실행된 동작은 PC에서 확인하세요."; }
        catch (Exception ex) { AppPaths.Log("phone-command", ex.GetType().Name); result = "처리 중 문제가 생겼어요. PC 화면에서 확인하세요."; }
        lock (gate)
        {
            try { ledger.Complete(receipt, result); }
            catch (Exception ex) { storageFailed = true; AppPaths.Log("phone-ledger", ex.GetType().Name); }
            finally { activeKey = null; activeStop = null; stop.Dispose(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (stopped) return; stopped = true; sessions.Clear(); lifetime.Cancel(); activeStop?.Cancel(); }
        if (host != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await host.StopAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            await host.DisposeAsync().ConfigureAwait(false); host = null;
        }
        try { await work.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { AppPaths.Log("phone-stop", "Command cancellation pending"); }
        lifetime.Dispose();
    }
}
