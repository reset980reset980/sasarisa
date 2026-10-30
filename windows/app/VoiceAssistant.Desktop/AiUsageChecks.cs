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

internal static class AiUsageChecks
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    static HttpResponseMessage Response(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    static AiUsageSample Sample(string provider = "claude", AiTokenUsage? tokens = null, string outcome = "response", DateTimeOffset? at = null) => new(Guid.NewGuid().ToString("N"), provider, at ?? DateTimeOffset.Now, 125, outcome, tokens ?? new());
    static AiDailyUsage Total(AiUsageStore store, string? provider = null) => AiUsageStore.Sum(store.Snapshot().State.Days.Values.SelectMany(x => x.Where(p => provider == null || p.Key == provider).Select(p => p.Value)));
    static object Quota(DateTimeOffset at, double percent = 42) => new { timestamp = at.ToString("O"), type = "event_msg", payload = new { type = "token_count", rate_limits = new { primary = new { used_percent = percent, window_minutes = 300, resets_at = at.AddHours(2).ToUnixTimeSeconds() } } } };

    internal static async Task<int> RunAsync(List<object> rows, bool native = false)
    {
        if (Path.GetFullPath(AppPaths.Home).Equals(Path.GetFullPath(AppPaths.StandardHome), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use an isolated test home.");
        var root = Path.Combine(AppPaths.Home, "ai-usage-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        int failed = 0;
        async Task Check(string name, Func<Task> run) { try { await run(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        Task Sync(Action run) { run(); return Task.CompletedTask; }
        AiUsageStore Store(string name) => new(Path.Combine(root, name + ".json"));

        await Check("AI usage separates Claude cache creation and read tokens without double counting", () => Sync(() =>
        {
            var value = AiUsageParsing.ClaudeTokens(Json("{\"usage\":{\"input_tokens\":10,\"output_tokens\":20,\"cache_read_input_tokens\":100,\"cache_creation_input_tokens\":30}}"));
            Require(value == new AiTokenUsage(10,20,100,30,true) && value.Total == 160 && value.Equivalent == 70, "Claude/cache weight is incorrect");
            Require(AiUsageParsing.ApiTokens(Json("{\"usage\":{\"input_tokens\":10,\"output_tokens\":20}}"), "anthropic") == new AiTokenUsage(10,20,0,0,true), "Anthropic mapping differs");
        }));
        await Check("AI usage subtracts inclusive cached input for Codex, Chat and Responses APIs", () => Sync(() =>
        {
            var codex = AiUsageParsing.CodexTokens(Json("{\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":60,\"output_tokens\":20}}"));
            var chat = AiUsageParsing.ApiTokens(Json("{\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":60}}}"), "compatible");
            var responses = AiUsageParsing.ApiTokens(Json("{\"usage\":{\"input_tokens\":100,\"output_tokens\":20,\"input_tokens_details\":{\"cached_tokens\":60}}}"), "openai");
            Require(codex == new AiTokenUsage(40,20,60,0,true) && chat == codex && responses == codex && codex.Total == 120, "Inclusive input double counted");
            Require(AiUsageParsing.ApiTokens(Json("{\"prompt_eval_count\":40,\"eval_count\":20}"), "ollama") == new AiTokenUsage(40,20,0,0,true), "Ollama native counters missed");
        }));
        await Check("AI usage keeps missing, negative, invalid and excessive counts unknown", () => Sync(() =>
        {
            foreach (var value in new[] { "{}", "{\"usage\":null}", "{\"usage\":{\"input_tokens\":10}}", "{\"usage\":{\"input_tokens\":-1,\"output_tokens\":1}}", "{\"usage\":{\"input_tokens\":1e100,\"output_tokens\":1}}", "{\"usage\":{\"input_tokens\":10,\"output_tokens\":1,\"cached_input_tokens\":11}}", "{\"usage\":{\"input_tokens\":10,\"output_tokens\":1,\"cached_input_tokens\":\"bad\"}}" })
                Require(!AiUsageParsing.CodexTokens(Json(value)).Reported, "Invalid Codex counts became known zero");
            Require(!AiUsageParsing.ClaudeTokens(Json("{\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"cache_read_input_tokens\":-1}}")).Reported, "Invalid Claude cache became zero");
            Require(!AiUsageParsing.ApiTokens(Json("{\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_tokens_details\":{\"cached_tokens\":99}}}"), "compatible").Reported, "Invalid API cache accepted");
        }));
        await Check("AI account quota parsing accepts only trusted provider event envelopes", () => Sync(() =>
        {
            var at = DateTimeOffset.Now;
            var codex = AiUsageParsing.CodexQuota(JsonSerializer.SerializeToElement(Quota(at)), at).Single(); Require(codex.Used == .42 && codex.Label == "5시간" && codex.Seen == at, "Codex quota mapping failed");
            var claude = AiUsageParsing.ClaudeQuota(Json("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"unifiedWindows\":{\"five_hour\":{\"utilization\":0.1,\"resetsAt\":1800000000},\"seven_day\":{\"utilization\":0.25}}}}"), at);
            Require(claude.Length == 2 && claude[0].Used == .1 && claude[0].Resets != null && claude[1].Resets == null, "Unknown reset was guessed");
            foreach (var type in new[] { "response_item", "item.completed", "message", "tool_result", "user" })
                Require(AiUsageParsing.CodexQuota(JsonSerializer.SerializeToElement(new { type, rate_limits = new { primary = new { used_percent = 99 } } }), at).Length == 0, "User/tool data spoofed account limits");
            Require(AiUsageParsing.CodexQuota(JsonSerializer.SerializeToElement(Quota(at, 101)), at).Length == 0, "Excess quota accepted");
            Require(AiUsageParsing.ClaudeQuota(Json("{\"type\":\"message\",\"rate_limit_info\":{\"rateLimitType\":\"five_hour\",\"utilization\":0.8}}"), at).Length == 0, "Wrong Claude envelope accepted");
        }));
        await Check("AI daily totals persist across restart, deduplicate retries and preserve unknown calls", () => Sync(() =>
        {
            var path = Path.Combine(root, "persist.json"); var store = new AiUsageStore(path); Require(!File.Exists(path), "Opening the store wrote an empty file");
            var sample = Sample(tokens: new(10,20,100,30,true)); Require(store.Record(sample) && store.Record(sample), "Record failed");
            store.Record(Sample("codex", outcome: "canceled")); store.Record(Sample("claude", outcome: "failed", at: DateTimeOffset.Now.AddDays(-1)));
            var copy = new AiUsageStore(path); var total = Total(copy); Require(total.Calls == 3 && total.ReportedCalls == 1 && total.Responses == 1 && total.Failed == 1 && total.Canceled == 1 && total.Tokens.Total == 160, "Persisted totals or dedup failed");
            Require(copy.Snapshot().State.Started <= DateTimeOffset.Now.AddHours(-23) && File.Exists(copy.BackupPath), "Recording coverage or backup lost");
        }));
        await Check("AI usage failure keeps accepted file and backup, queues once, then retries after unlock", () => Sync(() =>
        {
            var path = Path.Combine(root, "locked.json"); var store = new AiUsageStore(path); store.Record(Sample()); store.Record(Sample());
            var accepted = File.ReadAllText(path); var backup = File.ReadAllText(store.BackupPath); var next = Sample();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(!store.Record(next), "Locked write unexpectedly succeeded"); var pending = store.Snapshot();
                Require(pending.Error != null && pending.Pending == 1 && AiUsageStore.Sum(pending.State.Days.Values.SelectMany(x => x.Values)).Calls == 2 && File.ReadAllText(path) == accepted && File.ReadAllText(store.BackupPath) == backup, "Failure mutated accepted data or lost retry");
            }
            Require(store.Record(next) && Total(store).Calls == 3 && store.Snapshot().Pending == 0, "Retry duplicated or lost a call");
        }));
        await Check("AI usage merges external accepted saves instead of losing the other writer", () => Sync(() =>
        {
            var path = Path.Combine(root, "external.json"); var first = new AiUsageStore(path); var second = new AiUsageStore(path);
            first.Record(Sample()); second.Record(Sample("codex")); first.Record(Sample("ollama")); Require(Total(new(path)).Calls == 3, "External writer's request lost");
        }));
        await Check("Corrupt and oversized AI usage files remain intact and report pending writes", () => Sync(() =>
        {
            foreach (var text in new[] { "{broken", "{\"Version\":2}", "{\"Version\":1,\"Days\":null}", "{\"Version\":1,\"Version\":1}", "{\"Windows\":{\"codex|primary\":{\"Provider\":\"codex\",\"Key\":null}}}", new string(' ', AiUsageStore.MaxBytes + 1) })
            {
                var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(path, text); var store = new AiUsageStore(path);
                Require(!store.Record(Sample()) && store.Snapshot().Error != null && store.Snapshot().Pending == 1 && File.ReadAllText(path) == text && !File.Exists(store.BackupPath), "Invalid file overwritten or accepted silently");
            }
        }));
        await Check("AI usage reads BOM files, validates totals and rejects invented unknown token counts", () => Sync(() =>
        {
            var path = Path.Combine(root, "bom.json"); var store = new AiUsageStore(path); store.Record(Sample()); var text = File.ReadAllText(path); File.WriteAllText(path, text, new UTF8Encoding(true));
            Require(Total(new(path)).Calls == 1, "BOM changed fingerprint or parse");
            bool rejected = false; try { store.Record(Sample(tokens: new(1))); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Unreported counts were accepted");
        }));
        await Check("AI quota merge preserves separate windows, newest seen value and unknown reset", () => Sync(() =>
        {
            var store = Store("quotas"); var at = DateTimeOffset.Now; var first = new AiQuotaWindow("codex","primary","5시간",.5,null,at);
            store.Record(null, new[] { first, new AiQuotaWindow("codex","secondary","7일",.8,at.AddDays(1),at) }); store.Record(null, new[] { first with { Used = .1, Seen = at.AddMinutes(-1) } });
            var snapshot = store.Snapshot(); Require(snapshot.State.Windows.Count == 2 && snapshot.State.Windows["codex|primary"].Used == .5 && snapshot.State.Windows["codex|primary"].Resets == null && snapshot.State.Days.Count == 0 && snapshot.State.Started == null, "Quota updates fabricated requests or replaced a newer report");
        }));
        await Check("AI attempt stores metadata only, idempotent disposal and actual provider outcomes", () => Sync(() =>
        {
            var path = Path.Combine(root, "private.json"); var store = new AiUsageStore(path);
            var attempt = new AiUsageAttempt("codex", store); attempt.ObserveCodex(Json("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2},\"prompt\":\"private-prompt\",\"result\":\"private-answer\",\"key\":\"private-key\"}")); attempt.Complete(); attempt.Dispose(); attempt.Dispose();
            using (var canceled = new CancellationTokenSource()) { using var other = new AiUsageAttempt("aside", store, canceled.Token); canceled.Cancel(); }
            var text = File.ReadAllText(path); Require(!text.Contains("private-") && !text.Contains("prompt", StringComparison.OrdinalIgnoreCase) && Total(store).Calls == 2 && Total(store, "aside").Canceled == 1 && Total(store, "codex").ReportedCalls == 1, "Private content stored or outcome wrong");
        }));
        await Check("Claude streaming metadata preserves final structured plan and ignores verbose content", async () =>
        {
            var store = Store("stream"); using var usage = new AiUsageAttempt("claude", store);
            var stream = "{\"type\":\"assistant\",\"message\":\"private-stream-content\"}\ninvalid\n{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"rateLimitType\":\"five_hour\",\"utilization\":0.4}}\n{\"type\":\"result\",\"is_error\":false,\"structured_output\":{\"reply\":\"ok\",\"actions\":[]},\"usage\":{\"input_tokens\":10,\"output_tokens\":3}}\n";
            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stream))); var result = await ClaudeUsageEvents.ReadAsync(reader, usage);
            Require(result != null && Brain.Decode(result.Value.GetProperty("structured_output")).Reply == "ok", "Stream changed the structured plan"); usage.Complete(); usage.Dispose();
            Require(Total(store).Tokens.Total == 13 && store.Snapshot().State.Windows.Count == 1, "Stream metadata missing");
        });
        await Check("Claude streaming reader retains legacy result and rejects incomplete streams", async () =>
        {
            using var usage = new AiUsageAttempt("claude", Store("legacy"));
            using var legacy = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("{\"result\":\"legacy\"}\n"))); Require((await ClaudeUsageEvents.ReadAsync(legacy, usage))?.GetProperty("result").GetString() == "legacy", "Legacy final result lost");
            using var incomplete = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"assistant\",\"message\":\"incomplete\"}\n"))); Require(await ClaudeUsageEvents.ReadAsync(incomplete, usage) == null, "Partial message became a final result");
        });
        await Check("Claude stream output keeps restricted authority and no session persistence", () => Sync(() =>
        {
            var args = Brain.ClaudeInfo("fixture.exe", new()).ArgumentList.ToArray();
            string Next(string key) => args[Array.IndexOf(args, key) + 1];
            Require(args.Contains("--restricted") && Next("--tools") == "WebSearch" && args.Contains("--strict-mcp-config") && Next("--mcp-config") == "{\"mcpServers\":{}}" && args.Contains("--disable-slash-commands") && Next("--setting-sources") == "" && args.Contains("--no-session-persistence") && Next("--output-format") == "stream-json" && args.Contains("--verbose"), "Metadata expanded CLI authority");
        }));
        await Check("Codex quota reader bounds recent days, newest five files and tail bytes", async () =>
        {
            var at = DateTimeOffset.Now; var sessions = Path.Combine(root, "sessions");
            string Day(DateTimeOffset date) => Path.Combine(sessions, date.LocalDateTime.ToString("yyyy"), date.LocalDateTime.ToString("MM"), date.LocalDateTime.ToString("dd"));
            Directory.CreateDirectory(Day(at)); Directory.CreateDirectory(Day(at.AddDays(-4)));
            for (int i = 0; i < 7; i++) { var file = Path.Combine(Day(at), i + ".jsonl"); File.WriteAllText(file, new string('x', 410000) + "\n" + JsonSerializer.Serialize(Quota(at.AddMinutes(-i), 40+i)) + "\n{broken\n", new UTF8Encoding(false)); File.SetLastWriteTimeUtc(file, at.AddMinutes(-i).UtcDateTime); }
            File.WriteAllText(Path.Combine(Day(at.AddDays(-4)), "old.jsonl"), JsonSerializer.Serialize(Quota(at, 99)));
            var store = Store("scan"); var scan = await CodexUsageReader.RefreshAsync(store, sessions, at, default);
            Require(scan.Files == 5 && scan.Bytes == 2_000_000 && scan.Windows == 1 && scan.Error == null && store.Snapshot().State.Windows["codex|primary"].Used == .4, "Scan exceeded bounds or selected a stale report");
            Require(Total(store).Calls == 0, "Other Codex sessions counted as this app's calls");
        });
        await Check("Codex quota reader rejects future, old, user-content and malformed records", async () =>
        {
            var at = DateTimeOffset.Now; var sessions = Path.Combine(root, "untrusted"); var folder = Path.Combine(sessions, at.LocalDateTime.ToString("yyyy/MM/dd",System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "fixture.jsonl"), JsonSerializer.Serialize(Quota(at.AddHours(1))) + "\n" + JsonSerializer.Serialize(Quota(at.AddDays(-5))) + "\n" + "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"rate_limits\":{\"primary\":{\"used_percent\":99}}}}\n");
            var store = Store("untrusted-scan"); var scan = await CodexUsageReader.RefreshAsync(store, sessions, at, default); Require(scan.Files == 1 && scan.Windows == 0 && store.Snapshot().State.Windows.Count == 0, "Untrusted report promoted or fixture was not read");
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool stopped = false; try { await CodexUsageReader.RefreshAsync(store, sessions, at, canceled.Token); } catch (OperationCanceledException) { stopped = true; } Require(stopped && store.Snapshot().State.Windows.Count == 0, "Canceled refresh saved metadata");
        });
        await Check("API response usage counts actual model requests, excluding models metadata GET", async () =>
        {
            var before = Total(AiUsageStore.Shared, "compatible");
            using var client = new HttpClient(new Handler((request, _) => Task.FromResult(Response(request.Method == HttpMethod.Get ? (object)new { data = new[] { new { id = "fixture" } } } : new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { new { type = "function", function = new { name = "assistant_plan", arguments = "{\"reply\":\"ok\",\"actions\":[]}" } } } } } }, usage = new { prompt_tokens = 80, completion_tokens = 20 } }))));
            var settings = new UiSettings { BrainProvider = "compatible", BrainUrl = "http://127.0.0.1:1234/v1", BrainModel = "fixture" };
            await Brain.ModelsAsync(settings, default, "", client); await Brain.ApiAsync("private-request", settings, default, "private-key", client);
            var after = Total(AiUsageStore.Shared, "compatible"); Require(after.Calls == before.Calls + 1 && after.ReportedCalls == before.ReportedCalls + 1 && after.Tokens.Total == before.Tokens.Total + 100, "Metadata GET counted or response usage lost");
        });
        await Check("API usage preserves counts even if the resulting plan is rejected", async () =>
        {
            var before = Total(AiUsageStore.Shared, "openai"); using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 9, output_tokens = 3 } }))));
            bool rejected = false; try { await Brain.ApiAsync("fixture", new() { BrainProvider = "openai", BrainModel = "fixture" }, default, "fixture-key", client); } catch (InvalidOperationException) { rejected = true; }
            var after = Total(AiUsageStore.Shared, "openai"); Require(rejected && after.Calls == before.Calls + 1 && after.Responses == before.Responses + 1 && after.Tokens.Total == before.Tokens.Total + 12, "Rejected plan hid provider usage");
        });
        await Check("API usage records failed and canceled attempts with unavailable token counts", async () =>
        {
            var before = Total(AiUsageStore.Shared, "openai");
            using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new { error = "private-key" }, HttpStatusCode.TooManyRequests)))))
                try { await Brain.ApiAsync("fixture", new() { BrainProvider = "openai", BrainModel = "fixture" }, default, "fixture-key", client); } catch (InvalidOperationException) { }
            using (var cancellation = new CancellationTokenSource(30))
            using (var client = new HttpClient(new Handler(async (_, token) => { await Task.Delay(5000, token); return Response(new { }); })))
                try { await Brain.ApiAsync("fixture", new() { BrainProvider = "openai", BrainModel = "fixture" }, cancellation.Token, "fixture-key", client); } catch (OperationCanceledException) { }
            var after = Total(AiUsageStore.Shared, "openai"); Require(after.Calls == before.Calls + 2 && after.Failed == before.Failed + 1 && after.Canceled == before.Canceled + 1 && after.ReportedCalls == before.ReportedCalls, "API outcomes or unknown counts incorrect");
        });
        await Check("Direct API tools count each round under the actual provider without an extra router call", async () =>
        {
            var before = Total(AiUsageStore.Shared, "ollama"); var calls = 0; var dispatches = 0;
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(++calls == 1 ? (object)new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", tool_calls = new[] { new { id = "read", type = "function", function = new { name = "current_time", arguments = "{}" } } } } } }, usage = new { prompt_tokens = 10, completion_tokens = 2 } } : new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "done" } } }, usage = new { prompt_tokens = 20, completion_tokens = 3 } }))));
            var plan = await Brain.ApiToolsAsync("fixture", new() { BrainProvider = "ollama", BrainUrl = "http://127.0.0.1:11434", BrainModel = "fixture" }, (_, _) => { dispatches++; return Task.FromResult(new McpReply(false, "succeeded", "fixture")); }, default, "", client);
            var after = Total(AiUsageStore.Shared, "ollama"); Require(plan.Reply == "done" && dispatches == 1 && calls == 2 && after.Calls == before.Calls + 2 && after.Tokens.Total == before.Tokens.Total + 35, "Round counts/provider mapping incorrect");
        });
        if (native) await Check("Native restricted Claude streaming command returns a safe structured plan and usage metadata", async () =>
        {
            Directory.CreateDirectory(Path.Combine(AppPaths.Home, "work")); var before = Total(AiUsageStore.Shared, "claude");
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(125));
            var plan = await Brain.ClaudeAsync("Return only the required structured JSON object with reply equal to usage-smoke-ok and actions equal to an empty array. Do not use any tool, read any file, or perform any action.", new() { BrainModel = "" }, token.Token);
            var after = Total(AiUsageStore.Shared, "claude"); Require(plan.Actions.Count == 0 && plan.Reply.Contains("usage-smoke-ok") && after.Calls == before.Calls + 1 && after.ReportedCalls == before.ReportedCalls + 1 && after.Tokens.Total > before.Tokens.Total, "Actual CLI stream/schema or usage failed");
            rows.Add(new { measurement = "native Claude metadata", reportedTokens = after.Tokens.Total - before.Tokens.Total, reportedQuotaWindows = AiUsageStore.Shared.Snapshot().State.Windows.Values.Count(x => x.Provider == "claude"), note = "No account quota is inferred when the CLI omits a report." });
        });
        return failed;
    }
}
