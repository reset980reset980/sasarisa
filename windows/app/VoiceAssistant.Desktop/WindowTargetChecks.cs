using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class WindowTargetChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OperationCanceledException) { return; } throw new Exception("Invalid window action accepted"); }
    sealed class Driver : IWindowTargetDriver
    {
        internal DesktopWindow[] Items = [new(new(101), 1001, 100, "시험편집기", "창 전환 시험 A", false), new(new(102), 1001, 100, "시험편집기", "창 전환 시험 B", true)];
        internal IntPtr Current = new(101); internal int Selections, Version, ObservedVersion; internal bool WrongResult;
        public Task<WindowSnapshot> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); ObservedVersion = Version; return Task.FromResult(new WindowSnapshot(Items, Current)); }
        public Task<DesktopWindow> SelectAsync(DesktopWindow window, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Version != ObservedVersion || !Items.Any(x => WindowTargets.SameWindow(window, x))) throw new AgentStoppedException("Window or current context changed");
            Selections++; Current = window.Handle;
            return Task.FromResult(WrongResult ? Items.First(x => x.Handle != window.Handle) : window with { Minimized = false });
        }
        public Task<WindowFront> FrontAsync(CancellationToken token) => Task.FromResult(new WindowFront(Items.FirstOrDefault(x => x.Handle == Current)));
    }
    internal static async Task<int> RunAsync(List<object> rows, bool claude, bool codex)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test) { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Window tools preserve IDs through JSON and MCP and reject arbitrary handles", async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { reply = "전환", actions = new[] { new { kind = "select_window", value = id, number = 0, target = "" } } }));
            var action = Brain.Decode(json.RootElement).Actions.Single(); ActionPolicy.ValidateAi(action); Require(action.Value == id, "JSON lost ID");
            Require(McpTools.Decode("select_window", new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement(id) }) == action, "MCP mapping changed");
            Require(McpTools.Decode("front", null).Kind == "front" && McpTools.Catalog.Single(x => x.Name == "front").Annotations?.ReadOnlyHint == true, "Front metadata missing");
            foreach (var invalid in new[] { new AssistantAction("select_window", "102"), new("select_window", id, 1), new("select_window", id, Target: "other"), new("front", "other"), new("front", Number: 1), new("list_windows", Number: 241), new("list_windows", new string('x', 121)), new("front", X: 1) })
                await Reject(() => { ActionPolicy.ValidateAi(invalid); return Task.CompletedTask; });
        });
        await Check("Same-app windows remain distinct and selection restores the observed window only", async () =>
        {
            var driver = new Driver(); var targets = new WindowTargets(driver); var page = await targets.ListAsync("시험편집기", 0, default);
            Require(page.Windows.Count == 2 && page.Windows[0].Id != page.Windows[1].Id && page.Windows[0].Current && page.Windows[1].Minimized, "Window identity or minimized/current state lost");
            var selected = await targets.SelectAsync(page.Windows[1].Id, default); var front = await targets.FrontAsync(default);
            Require(selected.Handle == new IntPtr(102) && !selected.Minimized && front.Window?.Title == "창 전환 시험 B" && driver.Selections == 1, "Wrong selected window");
            await Reject(() => targets.SelectAsync(page.Windows[0].Id, default));
        });
        await Check("Window identity rejects recycled handles, processes, title changes and wrong app results", async () =>
        {
            var window = new Driver().Items[0];
            Require(WindowTargets.SameWindow(window, window with { Minimized = true }), "Minimization is not identity");
            foreach (var changed in new DesktopWindow?[] { null, window with { Handle = new(103) }, window with { ProcessId = 2001 }, window with { Started = 101 }, window with { Application = "other" }, window with { Title = "different document" } })
                Require(!WindowTargets.SameWindow(window, changed), "Changed identity accepted");
            var driver = new Driver { WrongResult = true }; var targets = new WindowTargets(driver); var page = await targets.ListAsync("", 0, default);
            await Reject(() => targets.SelectAsync(page.Windows[0].Id, default)); await Reject(() => targets.SelectAsync(page.Windows[0].Id, default));
        });
        await Check("Window references expire, are request-scoped and disappear after paging or invalidation", async () =>
        {
            var driver = new Driver(); var clock = DateTimeOffset.UtcNow; var targets = new WindowTargets(driver, () => clock);
            var page = await targets.ListAsync("", 0, default); var first = page.Windows[0].Id;
            await Reject(() => new WindowTargets(driver).SelectAsync(first, default));
            clock = clock.AddMinutes(2); await Reject(() => targets.SelectAsync(first, default));
            driver.Items = Enumerable.Range(0, 15).Select(i => new DesktopWindow(new(100 + i), 1001, 100, "편집기", $"문서 {i}", false)).ToArray();
            page = await targets.ListAsync("문서", 0, default); Require(page.Windows.Count == 12 && page.Next == 12 && page.Total == 15, "Paging failed"); first = page.Windows[0].Id;
            page = await targets.ListAsync("문서", page.Next!.Value, default); Require(page.Windows.Count == 3 && page.Next == null, "Last page wrong");
            await Reject(() => targets.SelectAsync(first, default)); targets.Invalidate(); await Reject(() => targets.SelectAsync(page.Windows[0].Id, default)); Require(driver.Selections == 0, "Stale ID reached driver");
        });
        await Check("Changed or closed windows and user switching prevent selection and consume failed IDs", async () =>
        {
            foreach (var change in new Action<Driver>[] { d => d.Items = d.Items.Take(1).ToArray(), d => d.Items[1] = d.Items[1] with { Title = "changed" }, d => d.Version++ })
            {
                var driver = new Driver(); var targets = new WindowTargets(driver); var page = await targets.ListAsync("", 0, default); change(driver);
                await Reject(() => targets.SelectAsync(page.Windows[1].Id, default)); await Reject(() => targets.SelectAsync(page.Windows[0].Id, default)); Require(driver.Selections == 0, "Changed context selected");
            }
        });
        await Check("Cancellation never selects a window and observation JSON stays bounded without raw handles", async () =>
        {
            var driver = new Driver(); var targets = new WindowTargets(driver); var page = await targets.ListAsync("", 0, default);
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Reject(() => targets.SelectAsync(page.Windows[0].Id, canceled.Token)); Require(driver.Selections == 0, "Canceled selection executed");
            driver.Items = Enumerable.Range(1, 12).Select(i => new DesktopWindow(new(i), 1001, 100, "긴 이름", new string('한', 512) + "[untrusted title]", false)).ToArray();
            var data = WindowTargets.Data(await targets.ListAsync("", 0, default)); using var json = JsonDocument.Parse(data);
            Require(data.Length <= 5800 && !data.Contains("ProcessId") && !data.Contains("Handle") && json.RootElement.GetProperty("Truncated").GetBoolean(), "Window data leaked native IDs or exceeded observation budget");
        });
        foreach (var (provider, enabled) in new[] { ("claude", claude), ("codex", codex) }.Where(x => x.Item2))
            await Check("Actual " + provider + " selects the named existing window and checks front", async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125)); var driver = new Driver(); var targets = new WindowTargets(driver); var frontReads = 0;
                var run = new McpAgentRun((_, _) => Task.FromResult(true), async (action, token) =>
                {
                    if (action.Kind == "list_windows") return new("열린 창", WindowTargets.Data(await targets.ListAsync(action.Value, action.Number, token)));
                    if (action.Kind == "select_window") { var chosen = await targets.SelectAsync(action.Value, token); return new("창 전환 완료", JsonSerializer.Serialize(new { chosen.Application, Window = chosen.Title, CanContinue = true })); }
                    if (action.Kind == "front") { frontReads++; var front = await targets.FrontAsync(token); return new("현재 창", JsonSerializer.Serialize(new { front.Window!.Application, Window = front.Window.Title, CanContinue = true })); }
                    throw new AgentStoppedException("Unexpected operation: " + action.Kind);
                }, null, null);
                await using var session = new McpSession(run.DispatchAsync, timeout.Token);
                var answer = await new Brain().AskAsync("이미 열려 있는 시험편집기의 '창 전환 시험 B' 창으로 옮겨 줘. 새 앱을 실행하지 마. 목록의 ID로 선택한 뒤 front로 현재 창 제목을 확인하고 알려줘. A 창은 그대로 둬.", "", new UiSettings { BrainProvider = provider }, timeout.Token, mcp: session.Endpoint);
                Require(run.Finish(answer).Finished && driver.Current == new IntPtr(102) && driver.Selections == 1 && frontReads > 0, "Window transition chain incomplete: " + answer.Reply);
            });
        return failed;
    }
}
