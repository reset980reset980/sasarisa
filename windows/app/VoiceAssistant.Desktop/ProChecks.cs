using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class ProChecks
{
    const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    sealed class Store { public string? Value; public int Saves, Deletes; public void Save(string value) { Value = value; Saves++; } public void Delete() { Value = null; Deletes++; } }
    static ProAccountClient Account(HttpClient http, Store store, Func<DateTimeOffset>? clock = null, TimeSpan? timeout = null) =>
        new(http, () => store.Value, store.Save, store.Delete, clock, TimeSpan.FromMilliseconds(1), timeout ?? TimeSpan.FromSeconds(5));
    static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    static HttpResponseMessage Start(string url = "https://maker.buffm.com/butler/link?code=TEST") => Json(new { code = "TEST", poll = "fixture-poll", url });
    static HttpResponseMessage Audio() { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(VoiceChecks.Silence()) }; r.Content.Headers.ContentType = new("audio/wav"); return r; }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task<Exception> Fails(Func<Task> action)
    { try { await action(); } catch (Exception ex) { return ex; } throw new Exception("Expected failure"); }
    static void Destination(HttpRequestMessage request, string path, bool auth)
    {
        Require(request.RequestUri?.AbsoluteUri == ProAccountClient.Server + "/api/butler/" + path, "Unexpected service destination");
        Require(request.Headers.Authorization?.ToString() == (auth ? "Bearer " + Key : null), "Credential destination mismatch");
    }
    public static async Task<int> RunAsync(List<object> rows, bool service = false)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        await Check("Pro link follows original start/poll protocol and stores only an approved key", async () =>
        {
            var store = new Store(); int polls = 0, codes = 0;
            using var http = new HttpClient(new Handler(async (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath.Split('/').Last();
                Destination(request, "link/" + path, false); Require(request.Method == HttpMethod.Post, "Link method");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                if (path == "start") { Require(body.RootElement.GetProperty("name").GetString() == "자비스" && body.RootElement.GetProperty("lang").GetString() == "ko", "Link identity"); return Start(); }
                Require(body.RootElement.GetProperty("poll").GetString() == "fixture-poll", "Poll secret missing");
                return ++polls == 1 ? Json(new { status = "pending" }) : Json(new { status = "ok", token = Key });
            }));
            using var account = Account(http, store);
            await account.LinkAsync("자비스", info => { codes++; Require(info.Code == "TEST" && store.Saves == 0, "Saved before approval"); }, CancellationToken.None);
            Require(codes == 1 && polls == 2 && store.Saves == 1 && account.Linked, "Link completion");
        });
        await Check("Pro rejects unsafe approval destinations and malformed approved keys", async () =>
        {
            foreach (var url in new[] { "http://maker.buffm.com/butler/link", "https://maker.buffm.com.evil.example/butler/link", "https://user@maker.buffm.com/butler/link", "https://maker.buffm.com:444/butler/link", "https://maker.buffm.com/elsewhere" })
            {
                var store = new Store(); int calls = 0;
                using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Start(url)); })); using var account = Account(http, store);
                await Fails(() => account.LinkAsync("자비스", _ => throw new Exception("Unsafe page shown"), CancellationToken.None));
                Require(calls == 1 && store.Saves == 0, "Unsafe approval accepted");
            }
            using var badHttp = new HttpClient(new Handler((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("start") ? Start() : Json(new { status = "ok", token = new string('!', 64) }))));
            var badStore = new Store(); using var bad = Account(badHttp, badStore);
            await Fails(() => bad.LinkAsync("자비스", _ => { }, CancellationToken.None)); Require(badStore.Saves == 0, "Malformed key persisted");
        });
        await Check("Pro cancelled polling cannot store a late approved response", async () =>
        {
            var store = new Store(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Handler(async (r, _) => { if (r.RequestUri!.AbsolutePath.EndsWith("start")) return Start(); entered.SetResult(); await release.Task; return Json(new { status = "ok", token = Key }); }));
            using var account = Account(http, store); using var stop = new CancellationTokenSource();
            var pending = account.LinkAsync("자비스", _ => { }, stop.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Cancel(); release.SetResult(); Require(await Fails(() => pending) is OperationCanceledException, "Cancellation not propagated");
            Require(store.Saves == 0 && !account.Linked, "Late credentials stored");
        });
        await Check("Pro expired codes and overall deadline terminate polling", async () =>
        {
            using var expiredHttp = new HttpClient(new Handler((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("start") ? Start() : new HttpResponseMessage(HttpStatusCode.NotFound))));
            var store = new Store(); using var expired = Account(expiredHttp, store);
            Require(await Fails(() => expired.LinkAsync("자비스", _ => { }, CancellationToken.None)) is InvalidOperationException, "Expiry not reported");
            using var timeoutHttp = new HttpClient(new Handler((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("start") ? Start() : Json(new { status = "pending" }))));
            using var timed = Account(timeoutHttp, store, timeout: TimeSpan.FromMilliseconds(50));
            Require(await Fails(() => timed.LinkAsync("자비스", _ => { }, CancellationToken.None)) is TimeoutException && store.Saves == 0, "Link timeout");
        });
        await Check("Pro status uses exact account and never retains stale authorization after failure or 401", async () =>
        {
            var store = new Store { Value = Key }; int count = 0;
            using var http = new HttpClient(new Handler((r, _) => { Destination(r, "me", true); return Task.FromResult(++count switch { 1 => Json(new { email = "fixture@example.invalid", pro = new { active = true, voice = true, plan = "fixture" }, voice = new { used = 11, limit = 25000 } }), 2 => new(HttpStatusCode.ServiceUnavailable), _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) }); }));
            using var account = Account(http, store);
            var first = await account.RefreshAsync(CancellationToken.None); Require(first.Active && first.Voice && first.Used == 11, "Membership parse");
            await Fails(() => account.RefreshAsync(CancellationToken.None)); Require(account.Status == null && account.Linked, "Stale membership or lost key on temporary error");
            await Fails(() => account.RefreshAsync(CancellationToken.None)); Require(!account.Linked && store.Deletes == 1, "Expired key retained");
        });
        await Check("Pro unlink clears local access immediately even when server revocation fails", async () =>
        {
            var store = new Store { Value = Key };
            using var http = new HttpClient(new Handler((r, _) => { Destination(r, "unlink", true); Require(store.Value == null, "Local access still active during revoke"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
            using var account = Account(http, store); Require(!await account.UnlinkAsync(CancellationToken.None) && !account.Linked && account.Status == null, "False revoke success");
        });
        await Check("Pro stale membership response cannot restore an unlinked account", async () =>
        {
            var store = new Store { Value = Key }; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Handler(async (r, _) => { if (r.RequestUri!.AbsolutePath.EndsWith("unlink")) return new(HttpStatusCode.NoContent); entered.SetResult(); await release.Task; return Json(new { pro = new { active = true, voice = true } }); }));
            using var account = Account(http, store); var pending = account.RefreshAsync(CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(await account.UnlinkAsync(CancellationToken.None), "204 revoke should succeed"); release.SetResult(); await Fails(() => pending);
            Require(!account.Linked && account.Status == null, "Stale membership restored");
        });
        await Check("Pro voice carries selected voice and common hint without a separate API key", async () =>
        {
            var store = new Store { Value = Key }; int calls = 0;
            using var http = new HttpClient(new Handler(async (r, _) => { Destination(r, "tts", true); using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()); Require(body.RootElement.GetProperty("voice").GetString() == ProAccountClient.Voices[1].Id && body.RootElement.GetProperty("common").GetBoolean() == (++calls == 1), "Voice/body mismatch"); return Audio(); }));
            using var account = Account(http, store); var p = new VoicePreferences { Provider = "pro", Voice = ProAccountClient.Voices[1].Id };
            Require((await account.FetchVoiceAsync("무엇을 도와드릴까요?", p, CancellationToken.None)).Extension == ".wav", "Audio parse");
            await account.FetchVoiceAsync("일반적인 답변입니다.", p, CancellationToken.None); Require(calls == 2, "Voice calls");
        });
        await Check("Pro quota or capacity errors back off for one hour; refresh explicitly retries", async () =>
        {
            foreach (var code in new[] { HttpStatusCode.PaymentRequired, HttpStatusCode.ServiceUnavailable })
            {
                var store = new Store { Value = Key }; int requests = 0; var now = DateTimeOffset.UtcNow;
                using var http = new HttpClient(new Handler((r, _) => { if (r.RequestUri!.AbsolutePath.EndsWith("me")) return Task.FromResult(Json(new { pro = new { voice = true } })); requests++; return Task.FromResult(new HttpResponseMessage(code)); }));
                using var account = Account(http, store, () => now); var p = new VoicePreferences { Provider = "pro" };
                await Fails(() => account.FetchVoiceAsync("시험", p, CancellationToken.None)); await Fails(() => account.FetchVoiceAsync("시험", p, CancellationToken.None)); Require(requests == 1, "Quota hammering");
                now = now.AddHours(1.1); await Fails(() => account.FetchVoiceAsync("시험", p, CancellationToken.None)); Require(requests == 2, "Backoff never expired");
                await account.RefreshAsync(CancellationToken.None); await Fails(() => account.FetchVoiceAsync("시험", p, CancellationToken.None)); Require(requests == 3, "Refresh did not release backoff");
            }
        });
        await Check("Pro voice authentication, invalid media and cancellation preserve fallback contract", async () =>
        {
            var p = new VoicePreferences { Provider = "pro", Fallback = false }; var store = new Store { Value = Key };
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))); using var account = Account(http, store);
            int native = 0; var result = await SpeechPipeline.RunAsync("시험", new() { Tts = p }, CancellationToken.None, account.FetchVoiceAsync, (_, _, _) => throw new Exception("Invalid audio played"), (_, _, _) => { native++; return Task.CompletedTask; });
            Require(result.UsedFallback && native == 1 && !account.Linked, "Unauthorized fallback failed");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Require(await Fails(() => SpeechPipeline.RunAsync("시험", new() { Tts = p }, cancelled.Token, account.FetchVoiceAsync, (_, _, _) => Task.CompletedTask, (_, _, _) => { native++; return Task.CompletedTask; })) is OperationCanceledException && native == 1, "Cancelled speech fell back");
            var mediaStore = new Store { Value = Key }; using var mediaHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { error = "not audio" })))); using var media = Account(mediaHttp, mediaStore);
            await Fails(() => media.FetchVoiceAsync("시험", p, CancellationToken.None));
        });
        await Check("Pro settings roundtrip preserves selected voice and never includes account credentials", () =>
        {
            var s = new UiSettings { Tts = new() { Provider = "pro", Voice = ProAccountClient.Voices[2].Id } }; SettingsStore.Validate(s);
            var json = SettingsTransfer.Serialize(s); Require(!json.Contains(Key) && !json.Contains("token", StringComparison.OrdinalIgnoreCase), "Credential exported");
            var copy = JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(s))!; Require(copy.Tts == s.Tts, "Voice lost");
            Require(SettingsTransfer.Prepare(copy, new(), false, false).Tts.Provider == "windows", "Imported network setting without consent"); return Task.CompletedTask;
        });
        await Check("Pro unlink rejects an in-flight voice response before playback", async () =>
        {
            var store = new Store { Value = Key }; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Handler(async (r, _) => { if (r.RequestUri!.AbsolutePath.EndsWith("unlink")) return new(HttpStatusCode.NoContent); entered.SetResult(); await release.Task; return Audio(); }));
            using var account = Account(http, store);
            var pending = account.FetchVoiceAsync("시험", new() { Provider = "pro" }, CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await account.UnlinkAsync(CancellationToken.None); release.SetResult();
            Require(await Fails(() => pending) is InvalidOperationException && !account.Linked, "Unlinked response reached playback");
        });
        await Check("Pro uses isolated Windows credential storage with exact roundtrip and deletion", () =>
        {
            var target = "VoiceAssistantWindows:api:butler-pro:test:" + Guid.NewGuid().ToString("N");
            try { CredentialStore.Save(target, Key); Require(CredentialStore.Read(target) == Key, "Native credential persistence"); }
            finally { CredentialStore.Delete(target); }
            Require(CredentialStore.Read(target) == null, "Native key not removed"); return Task.CompletedTask;
        });
        if (service) await Check("Pro actual HTTPS service issues a valid challenge; cancel stores no credentials", async () =>
        {
            var store = new Store(); using var http = ExternalVoice.CreateClient(); using var account = Account(http, store); using var stop = new CancellationTokenSource();
            bool received = false;
            var error = await Fails(() => account.LinkAsync("Windows 연결 시험", code => { received = code.Code.Length > 0 && code.Page.Host == "maker.buffm.com"; stop.Cancel(); }, stop.Token));
            Require(received && error is OperationCanceledException && store.Saves == 0 && !account.Linked, "Actual challenge/cancel failed");
        });
        return failed;
    }
}
