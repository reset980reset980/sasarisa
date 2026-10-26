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

internal static class ApiToolChecks
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    static void Require(bool value, string error) { if (!value) throw new Exception(error); }
    static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new[] { value.GetString()! },
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(p => Strings(p.Value)),
        _ => Array.Empty<string>()
    };
    static readonly string Image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=";
    static readonly string ImageId = new('a', 32);
    static object Calls(string provider, params (string Id, string Name, object Args)[] calls)
    {
        if (provider == "anthropic") return new { role = "assistant", stop_reason = "tool_use", content = new object[] { new { type = "thinking", thinking = "fixture", signature = "fixture-signature" } }.Concat(calls.Select(c => (object)new { type = "tool_use", id = c.Id, name = c.Name, input = c.Args })).ToArray() };
        if (provider == "openai") return new { status = "completed", output = new object[] { new { type = "reasoning", id = "r_" + calls[0].Id, summary = Array.Empty<object>(), encrypted_content = "fixture-encrypted" } }.Concat(calls.Select(c => (object)new { type = "function_call", call_id = c.Id, name = c.Name, arguments = JsonSerializer.Serialize(c.Args) })).ToArray() };
        return new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null, reasoning_content = "fixture-reasoning", tool_calls = calls.Select(c => new { type = "function", id = c.Id, function = new { name = c.Name, arguments = JsonSerializer.Serialize(c.Args) }, extra_content = new { google = new { thought_signature = "fixture-signature" } } }).ToArray() } } } };
    }
    static object Answer(string provider) => provider == "anthropic" ? (object)new { role = "assistant", stop_reason = "end_turn", content = new[] { new { type = "text", text = "도구 결과 확인 완료" } } }
        : provider == "openai" ? new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "도구 결과 확인 완료" } } } } }
        : new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "도구 결과 확인 완료" } } } };
    static UiSettings Settings(string provider) => new() { BrainProvider = provider, BrainModel = "fixture-model", BrainUrl = ApiProviders.DefaultUrl(provider) };
    internal static async Task<int> RunAsync(List<object> rows)
    {
        var failures = 0;
        async Task Check(string name, Func<Task> test)
        { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failures++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        foreach (var provider in ApiProviders.All.Select(x => x.Id).Append("ollama"))
            await Check(provider + " direct tools roundtrip with confirmed image, call IDs and provider context", async () =>
            {
                var requests = 0; var executed = new List<string>(); var approvals = new List<string>();
                using var client = new HttpClient(new Handler(async (request, token) =>
                {
                    requests++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement;
                    var route = provider == "anthropic" ? "/messages" : provider == "openai" ? "/responses" : "/chat/completions";
                    Require(request.RequestUri!.AbsolutePath.EndsWith(route) && root.GetProperty("model").GetString() == "fixture-model", "Wrong route/model");
                    Require(provider == "anthropic" ? request.Headers.GetValues("x-api-key").Single() == "test-key" : provider == "ollama" ? request.Headers.Authorization == null : request.Headers.Authorization?.Parameter == "test-key", "Wrong credential");
                    var tools = root.GetProperty("tools"); Require(tools.GetArrayLength() == McpTools.Catalog.Length, "Missing direct tools");
                    var first = tools[0]; Require(provider is "anthropic" or "openai" ? first.TryGetProperty("name", out _) : first.GetProperty("function").TryGetProperty("name", out _), "Wrong tool envelope");
                    if (provider == "openai") Require(!root.GetProperty("store").GetBoolean() && !root.GetProperty("parallel_tool_calls").GetBoolean(), "State storage or parallel execution enabled");
                    if (requests == 1) return Json(Calls(provider, ("shot", "screenshot", new { })));
                    var history = root.GetProperty(provider == "openai" ? "input" : "messages"); var wire = history.GetRawText();
                    Require(Strings(history).Any(x => x == Image || x == "data:image/png;base64," + Image) && wire.Contains(ImageId) && wire.Contains("shot") && wire.Contains("image/png"), "Image or result ID lost");
                    Require(wire.Contains(provider == "openai" ? "fixture-encrypted" : "fixture-signature"), "Provider reasoning/signature lost");
                    if (provider == "anthropic")
                    {
                        var result = history[2].GetProperty("content")[0]; Require(result.GetProperty("tool_use_id").GetString() == "shot" && result.GetProperty("content")[1].GetProperty("source").GetProperty("data").GetString() == Image, "Anthropic result linkage wrong");
                    }
                    else Require(wire.Contains(provider == "openai" ? "function_call_output" : "tool_call_id") && wire.Contains(provider == "openai" ? "input_image" : "image_url"), "Wrong result/image format");
                    return Json(requests == 2 ? Calls(provider, ("press", "click", new { screenshot_id = ImageId, x = 0, y = 0 })) : Answer(provider));
                }));
                var run = new McpAgentRun((action, _) => { approvals.Add(action.Kind); return Task.FromResult(true); }, (action, _) =>
                {
                    executed.Add(action.Kind); return Task.FromResult(action.Kind == "screenshot" ? new ActionOutcome("화면 조회", JsonSerializer.Serialize(new { screenshot_id = ImageId }), new("image/png", Image, 1, 1)) : new ActionOutcome("클릭 전달"));
                }, null, null);
                var answer = await Brain.ApiToolsAsync("disposable fixture", Settings(provider), run.DispatchAsync, default, "test-key", client);
                Require(run.Finish(answer).Finished && answer.Actions.Count == 0 && requests == 3 && approvals.SequenceEqual(new[] { "screenshot", "click" }) && executed.SequenceEqual(approvals), "Direct tool flow incomplete");
            });
        await Check("Direct tool malformed, duplicate, unknown and truncated batches execute nothing", async () =>
        {
            foreach (var provider in new[] { "compatible", "anthropic", "openai" })
            {
                var invalid = new object[]
                {
                    Calls(provider, ("first", "current_time", new { }), ("bad", "shell", new { value = "never-run" })),
                    Calls(provider, ("same", "current_time", new { }), ("same", "current_time", new { })),
                    Calls(provider, ("bad", "key", new { value = "win+r" })),
                    Calls(provider, Enumerable.Range(0, 7).Select(i => (i.ToString(), "current_time", (object)new { })).ToArray()),
                    JsonSerializer.Deserialize<JsonElement>("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"role\":\"assistant\",\"content\":\"incomplete\"}}]}"),
                    new { status = "incomplete", output = Array.Empty<object>() },
                    new { role = "assistant", stop_reason = "max_tokens", content = Array.Empty<object>() }
                };
                foreach (var response in invalid)
                {
                    var executions = 0; using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(response)))); var rejected = false;
                    try { await Brain.ApiToolsAsync("fixture", Settings(provider), (_, _) => { executions++; return Task.FromResult(new McpReply(false, "succeeded", "wrong")); }, default, "test-key", client); }
                    catch (InvalidOperationException) { rejected = true; }
                    Require(rejected && executions == 0, "Invalid batch executed");
                }
            }
        });
        await Check("Direct calls decline or target change stop remaining calls and network", async () =>
        {
            foreach (var blocked in new[] { false, true })
            {
                var requests = 0; var executed = 0; using var client = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Json(Calls("compatible", ("one", "key", new { value = "enter" }), ("two", "key", new { value = "enter" })))); }));
                var run = new McpAgentRun((_, _) => Task.FromResult(blocked), (_, _) => { executed++; throw new AgentStoppedException("target changed"); }, null, null);
                var answer = await Brain.ApiToolsAsync("fixture", Settings("compatible"), run.DispatchAsync, default, "", client);
                var result = run.Finish(answer); Require(!result.Finished && result.Declined != blocked && requests == 1 && executed == (blocked ? 1 : 0), "Blocked/declined calls continued");
            }
        });
        await Check("Failed direct call skips its dependent batch and returns all call results", async () =>
        {
            var requests = 0; var delivered = 0;
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                if (++requests == 1) return Json(Calls("compatible", ("a", "current_time", new { }), ("b", "current_time", new { })));
                var text = await request.Content!.ReadAsStringAsync(token); Require(text.Contains("skipped") && text.Contains("failed") && text.Contains("tool_call_id"), "Batch results not returned"); return Json(Answer("compatible"));
            }));
            var run = new McpAgentRun((_, _) => Task.FromResult(true), (_, _) => { delivered++; throw new InvalidOperationException("fixture failure"); }, null, null);
            var answer = await Brain.ApiToolsAsync("fixture", Settings("compatible"), run.DispatchAsync, default, "", client);
            Require(delivered == 1 && !run.Finish(answer).Finished && requests == 2, "Dependent operation executed or failure hidden");
        });
        await Check("Repeated direct call IDs cannot replay side effects", async () =>
        {
            var delivered = 0; using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Calls("compatible", ("same", "current_time", new { }))))));
            var rejected = false; try { await Brain.ApiToolsAsync("fixture", Settings("compatible"), (_, _) => { delivered++; return Task.FromResult(new McpReply(false, "succeeded", "ok")); }, default, "", client); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && delivered == 1, "Repeated call executed");
        });
        await Check("Direct approval cancellation causes no write and no next API request", async () =>
        {
            using var cancel = new CancellationTokenSource(); var requests = 0; var executed = 0;
            using var client = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Json(Calls("compatible", ("one", "key", new { value = "enter" })))); }));
            var run = new McpAgentRun(async (_, token) => { cancel.Cancel(); await Task.Delay(1000, token); return true; }, (_, _) => { executed++; return Task.FromResult(new ActionOutcome("wrong")); }, null, null);
            var stopped = false; try { await Brain.ApiToolsAsync("fixture", Settings("compatible"), run.DispatchAsync, cancel.Token, "", client); } catch (OperationCanceledException) { stopped = true; }
            Require(stopped && requests == 1 && executed == 0, "Cancellation continued");
        });
        await Check("Ollama direct calls discover local models without a credential", async () =>
        {
            var requests = 0; using var client = new HttpClient(new Handler(async (request, token) =>
            {
                Require(request.Headers.Authorization == null && request.RequestUri!.Host == "127.0.0.1", "Ollama leaked credential or host");
                if (++requests == 1) { Require(request.RequestUri!.AbsolutePath == "/v1/models", "Wrong models route"); return Json(new { data = new[] { new { id = "local-fixture" } } }); }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Require(body.RootElement.GetProperty("model").GetString() == "local-fixture", "Discovered model unused"); return Json(Answer("compatible"));
            }));
            var answer = await Brain.ApiToolsAsync("fixture", new() { BrainProvider = "ollama", BrainUrl = "http://127.0.0.1:11434", BrainModel = "" }, (_, _) => throw new Exception("Unexpected action"), default, "never-send", client);
            Require(requests == 2 && answer.Actions.Count == 0, "Discovery flow failed");
        });
        await Check("Direct tool rounds stop at the configured bound", async () =>
        {
            var requests = 0; var executed = 0; using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Calls("compatible", ((++requests).ToString(), "current_time", new { }))))));
            var stopped = false; try { await Brain.ApiToolsAsync("fixture", Settings("compatible"), (_, _) => { executed++; return Task.FromResult(new McpReply(false, "succeeded", "ok")); }, default, "", client); } catch (AgentStoppedException) { stopped = true; }
            Require(stopped && requests == AgentRun.MaxRounds && executed == requests, "Loop unbounded");
        });
        return failures;
    }
}
