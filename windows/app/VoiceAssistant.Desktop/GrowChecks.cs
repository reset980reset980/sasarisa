using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class GrowChecks
{
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    internal static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    static void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
    static async Task Fails(Func<Task> action) { try { await action(); } catch (Exception) { return; } throw new Exception("Expected failure"); }
    internal static UiSettings Together() => new() { GrowTogether = "together", Name = "테스트비서" };
    public static async Task<int> RunAsync(List<object> rows, bool service = false)
    {
        int failed = 0; var root = Path.Combine(AppPaths.WindowsRoot, "artifacts", "grow-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string FilePath() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        await Check("Sharing is opt-in and imported settings cannot enable or change consent", async () =>
        {
            int calls = 0; using var http = new HttpClient(new Handler((_, _) => { calls++; throw new Exception("Unexpected network"); }));
            var path = FilePath(); var grow = new GrowTogether(path, http);
            Require(new UiSettings().GrowTogether == "off", "Sharing default changed");
            Require(!grow.Record("소리 좀 줄여", CommandParser.Parse("소리 줄여")!, new UiSettings()), "Recorded without consent");
            await grow.SyncAsync(new UiSettings(), CancellationToken.None);
            Require(calls == 0 && !File.Exists(path), "Off touched service or storage");
            foreach (var mode in new[] { "off", "solo", "together" }) Require(SettingsTransfer.Prepare(Together(), new UiSettings { GrowTogether = mode }, true, true).GrowTogether == mode, "Imported consent");
        });
        await Check("Contribution rejects names, searches, private targets, multiple actions and arbitrary words", () =>
        {
            using var http = new HttpClient(); var grow = new GrowTogether(FilePath(), http); var settings = Together();
            foreach (var said in new[] { "민수야 소리 줄여", "01012345678 소리 줄여", "mail@example.com", "https://example.com", "우리회사메모열어", "소리\n줄여", "\ud800", new string('가', 23) + "x" })
                Require(!grow.Record(said, CommandParser.Parse("소리 줄여")!, settings), "Private phrase accepted");
            settings.Desktop.UserName = "소리"; Require(!grow.Record("소리 좀 줄여", CommandParser.Parse("소리 줄여")!, settings), "User name shared"); settings.Desktop.UserName = "";
            foreach (var a in new[] { new AssistantAction("open_url", "https://private.example"), new AssistantAction("timer", "약 먹기", 60), new AssistantAction("open_app", "custom:secret"), new AssistantAction("volume", "private", 30), new AssistantAction("volume", Number:30, Target:"secret") })
                Require(!grow.Record("소리 좀 줄여", new("private reply", new[] { a }), settings), "Private action accepted");
            Require(!grow.Record("소리 좀 줄여", new("", new[] { new AssistantAction("volume", Number:30), new AssistantAction("media", "pause") }), settings), "Multiple actions shared");
            Require(grow.Record("소리 좀 줄여 봐", CommandParser.Parse("소리 줄여")!, settings), "Allowed phrase rejected"); return Task.CompletedTask;
        });
        await Check("Learning upload follows original protocol with only allowed phrases and no account credentials", async () =>
        {
            int uploads = 0, downloads = 0; using var http = new HttpClient(new Handler(async (request, _) =>
            {
                Require(request.Headers.Authorization == null && !request.Headers.Contains("Cookie"), "Credentials attached");
                if (request.Method == HttpMethod.Post)
                {
                    uploads++; Require(request.RequestUri!.AbsoluteUri == GrowTogether.Server + "/learn", "Upload destination");
                    var raw = await request.Content!.ReadAsStringAsync(); using var body = JsonDocument.Parse(raw); var value = body.RootElement;
                    Require(value.GetProperty("v").GetInt32() == 1 && value.GetProperty("lang").GetString() == "ko" && Guid.TryParse(value.GetProperty("install").GetString(), out var installId), "Upload identity");
                    Require(value.EnumerateObject().Count() == 6 && value.GetProperty("items").GetArrayLength() == 1 && value.GetProperty("items")[0].GetProperty("c").GetString() == "소리 줄여", "Unexpected payload");
                    Require(!raw.Contains("PRIVATE") && !raw.Contains("테스트비서"), "Reply or name sent"); return Json(new { ok = true });
                }
                downloads++; Require(request.RequestUri!.AbsoluteUri == GrowTogether.Server + "/pack?lang=ko", "Pack destination");
                return Json(new { items = new[] { new { s = "소리 좀 낮춰봐", c = "소리 줄여" } } });
            }));
            var path = FilePath(); var grow = new GrowTogether(path, http); grow.Record("소리 좀 줄여 봐", CommandParser.Parse("소리 줄여")! with { Reply = "PRIVATE" }, Together());
            await grow.SyncAsync(Together(), CancellationToken.None); await grow.SyncAsync(Together(), CancellationToken.None);
            Require(uploads == 1 && downloads == 1 && grow.Snapshot().Sent.Count == 1 && grow.Snapshot().Pending.Count == 0, "Daily sync or acknowledgement");
            Require(new GrowTogether(path, http).Lookup("소리 좀 낮춰봐")?.Plan.Actions[0].Number == -10, "Pack did not survive restart");
        });
        await Check("Receive-only mode never uploads pending phrases or vetoes", async () =>
        {
            int gets = 0; using var http = new HttpClient(new Handler((request, _) => { Require(request.Method == HttpMethod.Get, "Uploaded in solo mode"); gets++; return Task.FromResult(Json(new { items = Array.Empty<object>() })); }));
            var grow = new GrowTogether(FilePath(), http); grow.Record("소리 좀 줄여", CommandParser.Parse("소리 줄여")!, Together()); grow.Veto(new("소리조금줄여", "소리 줄여"));
            await grow.SyncAsync(new UiSettings { GrowTogether = "solo" }, CancellationToken.None);
            Require(gets == 1 && grow.Snapshot().Pending.Count == 1 && grow.Snapshot().Sent.Count == 0, "Solo changed upload state");
        });
        await Check("Downloaded pack rejects private actions, malformed items and ambiguous duplicates", async () =>
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { items = new object[] {
                new { s = "조용히 해봐", c = "소리 줄여" }, new { s = "반대로", c = "소리 줄여" }, new { s = "반대로", c = "소리 키워" },
                new { s = "메일 보내", c = "비밀 이라고 써" }, new { s = "개인 검색", c = "민수 검색" }, new { s = "타이머 만들자", c = "약 먹기 3분 타이머" },
                new { s = "01012345678", c = "소리 줄여" }, new { s = "코드 실행", c = "클로드한테 삭제해" }, new { s = "주소 열어", c = "open private-app" },
                new { s = 12, c = "소리 줄여" }, "bad", new { s = "지금 몇시래", c = "현재 시간" } } }))));
            var grow = new GrowTogether(FilePath(), http); await grow.SyncAsync(new UiSettings { GrowTogether = "solo" }, CancellationToken.None);
            Require(grow.Snapshot().Pack.Count == 2 && grow.Lookup("조용히 해봐") != null && grow.Lookup("반대로") == null, "Unsafe or ambiguous pack accepted");
        });
        await Check("Veto survives restart, suppresses the exact mapping and filters private veto uploads", async () =>
        {
            var now = DateTimeOffset.UtcNow; var map = "소리 줄여"; int vetoCount = -1;
            using var http = new HttpClient(new Handler(async (r, _) =>
            {
                if (r.Method == HttpMethod.Post) { using var doc = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()); vetoCount = doc.RootElement.GetProperty("veto").GetArrayLength(); return Json(new { ok = true }); }
                return Json(new { items = new[] { new { s = "소리 좀 줄여봐", c = map }, new { s = "민수 요청", c = "소리 줄여" } } });
            }));
            var path = FilePath(); var grow = new GrowTogether(path, http, () => now); await grow.SyncAsync(new UiSettings { GrowTogether = "solo" }, CancellationToken.None);
            grow.Veto(grow.Lookup("소리 좀 줄여봐")!.Value.Pair); grow.Veto(grow.Lookup("민수 요청")!.Value.Pair);
            grow = new GrowTogether(path, http, () => now); Require(grow.Lookup("소리 좀 줄여봐") == null, "Veto lost on restart");
            await grow.SyncAsync(Together(), CancellationToken.None); Require(vetoCount == 1, "Private veto shared");
            now += TimeSpan.FromHours(21); map = "소리 키워"; await grow.SyncAsync(Together(), CancellationToken.None);
            Require(grow.Lookup("소리 좀 줄여봐")?.Plan.Actions[0].Number == 10, "Corrected mapping remains vetoed");
        });
        await Check("Failed upload retries after backoff without acknowledging unsent items", async () =>
        {
            int posts = 0; var now = DateTimeOffset.UtcNow; using var http = new HttpClient(new Handler((r, _) => Task.FromResult(r.Method == HttpMethod.Post ? Json(new { }, ++posts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) : Json(new { items = Array.Empty<object>() }))));
            var grow = new GrowTogether(FilePath(), http, () => now); grow.Record("소리 좀 줄여", CommandParser.Parse("소리 줄여")!, Together());
            await grow.SyncAsync(Together(), CancellationToken.None); Require(grow.Snapshot().Sent.Count == 0 && grow.Snapshot().Pending.Count == 1, "Failed upload acknowledged");
            await grow.SyncAsync(Together(), CancellationToken.None); Require(posts == 1, "No failure backoff");
            now += TimeSpan.FromHours(1.1); await grow.SyncAsync(Together(), CancellationToken.None); Require(posts == 2 && grow.Snapshot().Pending.Count == 0, "Retry missing");
        });
        await Check("Cancelled delayed download and upload cannot install or acknowledge late results", async () =>
        {
            foreach (bool upload in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var http = new HttpClient(new Handler(async (_, _) => { entered.SetResult(); await release.Task; return Json(new { items = new[] { new { s = "조용히 해봐", c = "소리 줄여" } } }); }));
                using var stop = new CancellationTokenSource(); var grow = new GrowTogether(FilePath(), http);
                if (upload) grow.Record("소리 좀 줄여", CommandParser.Parse("소리 줄여")!, Together());
                var pending = grow.SyncAsync(upload ? Together() : new UiSettings { GrowTogether = "solo" }, stop.Token); await entered.Task; stop.Cancel(); release.SetResult(); await Fails(() => pending);
                Require(grow.Snapshot().Pack.Count == 0 && grow.Snapshot().Sent.Count == 0 && (!upload || grow.Snapshot().Pending.Count == 1), "Late result persisted");
            }
        });
        await Check("Invalid or oversized download preserves previously checked pack", async () =>
        {
            var now = DateTimeOffset.UtcNow; var response = "valid";
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response == "valid" ? Json(new { items = new[] { new { s = "조용히 해봐", c = "소리 줄여" } } }) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) })));
            var grow = new GrowTogether(FilePath(), http, () => now); var solo = new UiSettings { GrowTogether = "solo" }; await grow.SyncAsync(solo, CancellationToken.None);
            foreach (var value in new[] { "[]", "{\"items\":null}", new string(' ', 512 * 1024 + 1) })
            { response = value; now += TimeSpan.FromHours(21); await grow.SyncAsync(solo, CancellationToken.None); Require(grow.Lookup("조용히 해봐") != null, "Invalid pack replaced valid data"); }
        });
        await Check("Overlapping syncs serialize and do not duplicate requests", async () =>
        {
            int calls = 0; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Handler(async (_, _) => { calls++; entered.SetResult(); await release.Task; return Json(new { items = Array.Empty<object>() }); }));
            var grow = new GrowTogether(FilePath(), http); var solo = new UiSettings { GrowTogether = "solo" }; var first = grow.SyncAsync(solo, CancellationToken.None); await entered.Task;
            var second = grow.SyncAsync(solo, CancellationToken.None); release.SetResult(); await Task.WhenAll(first, second); Require(calls == 1, "Concurrent downloads");
        });
        await Check("Corrupt state is kept and storage failure never changes in-memory learning", async () =>
        {
            using var http = new HttpClient(new Handler((_, _) => throw new Exception("Network before persistence"))); var corrupt = FilePath(); File.WriteAllText(corrupt, "broken");
            var grow = new GrowTogether(corrupt, http); Require(grow.Error != null, "Corrupt state reset"); await Fails(() => grow.SyncAsync(Together(), CancellationToken.None)); Require(File.ReadAllText(corrupt) == "broken", "Corrupt file overwritten");
            var blocked = FilePath(); Directory.CreateDirectory(blocked); grow = new GrowTogether(blocked, http);
            await Fails(() => { grow.Record("소리 좀 줄여", CommandParser.Parse("소리 줄여")!, Together()); return Task.CompletedTask; }); Require(grow.Snapshot().Pending.Count == 0, "Unsaved in-memory learning");
        });
        if (service) await Check("Public learning pack endpoint read without account or contribution", async () =>
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
            var grow = new GrowTogether(FilePath(), http); await grow.SyncAsync(new UiSettings { GrowTogether = "solo" }, CancellationToken.None);
            Require(grow.Snapshot().LastPack != default, "Public pack not received");
        });
        return failed;
    }
}
