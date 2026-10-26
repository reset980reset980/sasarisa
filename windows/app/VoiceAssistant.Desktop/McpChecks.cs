using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace VoiceAssistant.Desktop;

internal static class McpChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Reject(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException) { return; } throw new Exception("Invalid operation accepted"); }
    static McpReply Reply(CallToolResult result) => JsonSerializer.Deserialize<McpReply>(result.Content.OfType<TextContentBlock>().Single().Text)!;
    internal static Task<McpClient> Connect(McpEndpoint endpoint, string version, CancellationToken token) => McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Name = "sasarisa-check", Command = Environment.ProcessPath!, Arguments = ["--mcp"],
        EnvironmentVariables = endpoint.Environment, ShutdownTimeout = TimeSpan.FromSeconds(3)
    }), new() { ProtocolVersion = version, InitializationTimeout = TimeSpan.FromSeconds(15) }, cancellationToken: token);

    internal static async Task<int> RunAsync(List<object> rows, bool claude, bool codex)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test)
        { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("MCP catalogue covers only implemented AI actions with strict arguments", async () =>
        {
            Require(McpTools.Catalog.Select(x => x.Name).Order().SequenceEqual(ActionPolicy.AiKinds.Where(x => x != "answer").Order()), "Catalogue differs from action policy");
            Require(McpTools.Decode("current_time", null).Kind == "current_time", "No-argument tool failed");
            foreach (var (name, args) in new[] { ("unimplemented_tool", "{}"), ("timer", "{}"), ("timer", "{\"number\":0}"), ("current_time", "{\"command\":\"evil\"}"), ("fill_field", "{\"target\":\"fake\",\"value\":\"text\"}"), ("type", "{\"value\":null}") })
                await Reject(() => { McpTools.Decode(name, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args)); return Task.CompletedTask; });
        });
        foreach (var version in new[] { "2025-06-18", "2025-11-25", "2026-07-28" })
            await Check("MCP real stdio negotiation, list, Korean result and errors " + version, async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var calls = 0;
                await using var session = new McpSession((action, token) => { calls++; return Task.FromResult(new McpReply(false, "succeeded", "연결 성공", "{\"fixture\":true}")); }, deadline.Token);
                await using var client = await Connect(session.Endpoint, version, deadline.Token);
                var catalog = await client.ListToolsAsync(cancellationToken: deadline.Token);
                Require(catalog.Count == McpTools.Catalog.Length, "Tool list incomplete");
                var result = Reply(await client.CallToolAsync("current_time", cancellationToken: deadline.Token));
                Require(!result.IsError && result.Summary == "연결 성공" && result.Data!.Contains("fixture"), "Tool result damaged");
                var invalid = await client.CallToolAsync(new CallToolRequestParams { Name = "timer", Arguments = new Dictionary<string, JsonElement> { ["number"] = JsonSerializer.SerializeToElement(0) } }, deadline.Token);
                Require(invalid.IsError == true && calls == 1, "Invalid action reached dispatcher");
                var unknown = await client.CallToolAsync(new CallToolRequestParams { Name = "unimplemented_tool" }, deadline.Token);
                Require(unknown.IsError == true && calls == 1, "Unknown tool reached dispatcher");
            });
        await Check("MCP same-user pipe secret and concurrent call serialization", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var active = 0; var maximum = 0; var count = 0;
            await using var session = new McpSession(async (_, token) =>
            { active++; maximum = Math.Max(maximum, active); try { await Task.Delay(25, token); count++; return new(false, "succeeded", "ok"); } finally { active--; } }, deadline.Token);
            var bad = await McpSession.CallAsync(session.Endpoint with { Secret = new string('0', 64) }, new("current_time"), deadline.Token);
            Require(bad.IsError && count == 0, "Wrong secret authorized");
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => McpSession.CallAsync(session.Endpoint, new("current_time"), deadline.Token)));
            Require(results.All(x => !x.IsError) && count == 6 && maximum == 1, "Concurrent side effects overlapped");
        });
        await Check("MCP real stdio cancellation reaches active app call and connection remains usable", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = new McpSession(async (action, token) =>
            {
                if (action.Kind == "list_timers")
                { started.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { cancelled.TrySetResult(); throw; } }
                return new(false, "succeeded", "ok");
            }, deadline.Token);
            await using var client = await Connect(session.Endpoint, "2026-07-28", deadline.Token);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var pending = client.CallToolAsync("list_timers", cancellationToken: call.Token);
            await started.Task.WaitAsync(deadline.Token); call.Cancel();
            await Reject(async () => { await pending; }); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(Reply(await client.CallToolAsync("current_time", cancellationToken: deadline.Token)).Summary == "ok", "Server unusable after cancellation");
        });
        await Check("MCP EOF exits the actual headless app process without opening UI", async () =>
        {
            await using var session = new McpSession((_, _) => Task.FromResult(new McpReply(false, "succeeded", "ok")), default);
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--mcp"); foreach (var pair in session.Endpoint.Environment) info.Environment[pair.Key] = pair.Value;
            using var process = Process.Start(info)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await process.WaitForExitAsync(timeout.Token);
                Require(process.ExitCode == 0 && string.IsNullOrWhiteSpace(await output), "EOF failed: " + await error);
            }
            finally { if (!process.HasExited) process.Kill(true); }
        });
        await Check("MCP rejection, window guard and action budget stop later calls", async () =>
        {
            var executions = 0;
            Task<ActionOutcome> Execute(AssistantAction a, CancellationToken t) { executions++; return Task.FromResult(new ActionOutcome("done")); }
            var decline = new McpAgentRun((_, _) => Task.FromResult(false), Execute, null, null);
            Require((await decline.DispatchAsync(new("type", "text"), default)).Status == "declined", "Refusal not reported");
            Require((await decline.DispatchAsync(new("current_time"), default)).IsError && executions == 0, "Action after refusal");
            Require(decline.Finish(new("완료", Array.Empty<AssistantAction>())).Declined, "False completion after refusal");
            var guard = new McpAgentRun((_, _) => Task.FromResult(true), (_, _) => throw new AgentStoppedException("changed window"), null, null);
            Require((await guard.DispatchAsync(new("read_screen"), default)).Status == "blocked", "Guard failed");
            Require(!guard.Finish(new("완료", Array.Empty<AssistantAction>())).Finished, "False completion after guard");
            var budget = new McpAgentRun((_, _) => Task.FromResult(true), Execute, null, null);
            for (var i = 0; i < AgentRun.MaxRounds * AgentRun.MaxActionsPerRound; i++) Require(!(await budget.DispatchAsync(new("current_time"), default)).IsError, "Premature limit");
            Require((await budget.DispatchAsync(new("current_time"), default)).IsError && executions == 72, "Budget bypassed");
        });
        foreach (var provider in new[] { ("claude", claude), ("codex", codex) }.Where(x => x.Item2))
            await Check("MCP actual " + provider.Item1 + " observes IDs then cancels only the matching mock timer", async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(125));
                var first = Guid.NewGuid().ToString(); var second = Guid.NewGuid().ToString(); var listed = false; var cancelled = false;
                var run = new McpAgentRun((_, _) => Task.FromResult(true), (action, token) =>
                {
                    if (action.Kind == "list_timers") { listed = true; return Task.FromResult(new ActionOutcome("2개", JsonSerializer.Serialize(new[] { new { id = first, label = "연결검사" }, new { id = second, label = "유지" } }))); }
                    if (action.Kind == "cancel_timer" && listed && action.Value == first && !cancelled) { cancelled = true; return Task.FromResult(new ActionOutcome("연결검사 타이머 취소됨")); }
                    throw new AgentStoppedException("Unexpected test action: " + action.Kind);
                }, null, null);
                await using var session = new McpSession(run.DispatchAsync, deadline.Token);
                var plan = await new Brain().AskAsync("현재 타이머 목록에서 이름이 '연결검사'인 것 하나만 취소해. 다른 타이머는 유지해. 도구를 실제 호출해.", "", new UiSettings { BrainProvider = provider.Item1 }, deadline.Token, mcp: session.Endpoint);
                var result = run.Finish(plan);
                Require(listed && cancelled && result.Finished && result.Observations.Count == 2, "MCP actions incomplete: " + result.Reply + "; steps=" + result.Observations.Count);
            });
        return failed;
    }
}
