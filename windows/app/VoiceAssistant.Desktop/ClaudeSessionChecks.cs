using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class ClaudeSessionChecks
{
    sealed class Driver : IClaudeSessionDriver
    {
        public ClaudeSession[] Items = Enumerable.Range(0, 15).Select(i => new ClaudeSession("key-" + i, "세션 " + i, "시험 프로젝트")).ToArray();
        public int Selections, Sends;
        public bool DraftChanged;
        public Task<ClaudeSessionSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(new ClaudeSessionSnapshot(Items));
        public Task<SelectedClaudeSession> SelectAsync(ClaudeSession session, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Selections++; return Task.FromResult(new SelectedClaudeSession(session.Title, session.Project, "https://claude.ai/epitaxy/session_test")); }
        public Task<SelectedClaudeSession> PrepareAsync(ClaudeSession session, CancellationToken token) => SelectAsync(session, token);
        public Task SendAsync(SelectedClaudeSession selected, string text, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (DraftChanged) throw new AgentStoppedException("초안 변경"); Sends++; return Task.CompletedTask; }
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Stopped(Func<Task> work)
    { try { await work(); } catch (AgentStoppedException) { return; } throw new Exception("Expected a stop"); }
    public static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> test)
        { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        await Check("Claude Code landing pages allow discovery but never qualify as send destinations", () =>
        {
            foreach (var address in new[] { "https://claude.ai/epitaxy", "https://claude.ai/epitaxy/", "https://claude.ai/epitaxy?coldLaunch=1" })
                Require(ClaudeSessions.IsCodeHomeAddress(address) && !ClaudeSessions.IsCodeAddress(address), "Landing page scope incorrect");
            foreach (var address in new[] { "https://claude.ai/new", "https://claude.ai/epitaxy?next=other",
                "https://claude.ai/epitaxy#other", "https://claude.ai:8443/epitaxy", "https://claude.ai.example/epitaxy",
                "https://user@claude.ai/epitaxy", "https://claude.ai/epitaxy/session_test" })
                Require(!ClaudeSessions.IsCodeHomeAddress(address), "Unrecognized landing address accepted: " + address);
            return Task.CompletedTask;
        });
        await Check("Claude Code accepts observed cloud and local session addresses only", () =>
        {
            Require(ClaudeSessions.IsCodeAddress("https://claude.ai/epitaxy/session_01Wy4DHomJHmUVWfL151XFWt"), "Cloud session rejected");
            Require(ClaudeSessions.IsCodeAddress("https://claude.ai/epitaxy/local_73e8990a-0c8d-492c-962b-24027edb67d5"), "Local session rejected");
            foreach (var address in new[] { "http://claude.ai/epitaxy/session_test", "https://claude.ai.example/epitaxy/session_test",
                "https://user@claude.ai/epitaxy/session_test", "https://claude.ai:8443/epitaxy/session_test",
                "https://claude.ai/epitaxy/local_bad", "https://claude.ai/chat/session_test",
                "https://claude.ai/epitaxy/session_test?next=other", "https://claude.ai/epitaxy/session_test#other" })
                Require(!ClaudeSessions.IsCodeAddress(address), "Unrecognized address accepted: " + address);
            return Task.CompletedTask;
        });
        await Check("Claude session list is paged and only the latest observed IDs can select", async () =>
        {
            var driver = new Driver(); var sessions = new ClaudeSessions(driver);
            var first = await sessions.ListAsync("시험", 0, default); Require(first.Sessions.Count == 12 && first.Next == 12 && first.Total == 15, "First page missing");
            var second = await sessions.ListAsync("시험", first.Next!.Value, default); Require(second.Sessions.Count == 3 && second.Next == null, "Next page missing");
            await Stopped(() => sessions.SelectAsync(first.Sessions[0].Id, default));
            var selected = await sessions.SelectAsync(second.Sessions[1].Id, default); Require(selected.Title == "세션 13" && driver.Selections == 1, "Wrong session selected");
        });
        await Check("Claude session IDs expire and cannot be guessed from titles", async () =>
        {
            var time = DateTimeOffset.UtcNow; var driver = new Driver(); var sessions = new ClaudeSessions(driver, () => time);
            var page = await sessions.ListAsync("세션 1", 0, default);
            await Stopped(() => sessions.SelectAsync("세션 1", default)); time = time.AddMinutes(2);
            await Stopped(() => sessions.SelectAsync(page.Sessions[0].Id, default)); Require(driver.Selections == 0, "Expired identity acted");
        });
        await Check("Claude delegation consumes one exact approved payload", async () =>
        {
            var driver = new Driver(); var sessions = new ClaudeSessions(driver); var page = await sessions.ListAsync("", 0, default);
            var description = await sessions.PrepareAsync(page.Sessions[0].Id, "승인한 작업", default);
            Require(description.Contains("세션 0") && description.Contains("승인한 작업"), "Approval omitted destination or text");
            await Stopped(() => sessions.SendAsync("바뀐 작업", default)); await Stopped(() => sessions.SendAsync("승인한 작업", default));
            Require(driver.Sends == 0, "Changed content sent");
            page = await sessions.ListAsync("", 0, default); await sessions.PrepareAsync(page.Sessions[0].Id, "승인한 작업", default);
            await sessions.SendAsync("승인한 작업", default); await Stopped(() => sessions.SendAsync("승인한 작업", default)); Require(driver.Sends == 1, "Send replayed");
        });
        foreach (var mode in new[] { "decline", "cancel", "changed" })
            await Check("Claude delegation " + mode + " cannot send or report completion", async () =>
            {
                var driver = new Driver(); var sessions = new ClaudeSessions(driver); var page = await sessions.ListAsync("", 0, default); using var cancel = new CancellationTokenSource();
                var action = new AssistantAction("delegate_session", "연결 확인", Target: page.Sessions[0].Id);
                var run = new McpAgentRun(async (a, token) => { await sessions.PrepareAsync(a.Target, a.Value, token); if (mode == "cancel") cancel.Cancel(); driver.DraftChanged = mode == "changed"; return mode != "decline"; },
                    async (a, token) => { await sessions.SendAsync(a.Value, token); return new ActionOutcome("접수"); }, null, null);
                try { var reply = await run.DispatchAsync(action, cancel.Token); Require(reply.IsError, "Failed handoff succeeded"); }
                catch (OperationCanceledException) when (mode == "cancel") { }
                Require(driver.Sends == 0 && !run.Finish(new("완료", Array.Empty<AssistantAction>())).Finished, "Failed send was completed");
                if (mode != "cancel") { var next = await run.DispatchAsync(action, default); Require(next.Status == "blocked", "Stopped handoff continued"); }
            });
        await Check("Claude session actions retain protected-app policy and require observed IDs", () =>
        {
            Require(WindowsActions.IsProtectedProcess("claude"), "Generic Claude protection removed");
            Require(ActionPolicy.NeedsConfirmation(new("delegate_session", "작업", Target: Guid.NewGuid().ToString("N")), true), "Delegation not reviewed");
            Require(McpTools.Catalog.Any(x => x.Name == "list_sessions") && McpTools.Catalog.Any(x => x.Name == "switch_session") && McpTools.Catalog.Any(x => x.Name == "delegate_session"), "Missing tools");
            foreach (var action in new[] { new AssistantAction("switch_session", "title"), new AssistantAction("delegate_session", "작업", Target: "title"), new AssistantAction("delegate_session", "", Target: Guid.NewGuid().ToString("N")), new AssistantAction("list_sessions", Number: 999) })
            { bool rejected = false; try { ActionPolicy.ValidateAi(action); } catch (InvalidOperationException) { rejected = true; } Require(rejected, "Invalid session request accepted"); }
            return Task.CompletedTask;
        });
        return failed;
    }
}
