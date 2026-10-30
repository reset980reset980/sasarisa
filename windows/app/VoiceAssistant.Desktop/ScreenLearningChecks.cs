using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class ScreenLearningChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
        throw new Exception("Invalid stored action was accepted");
    }
    static DesktopControl Field(string key = "old-id", string name = "본문", string context = "편집", bool sensitive = false) => new(key, "Edit", name, context, "", true, "original", "revision-1", 0, 0, sensitive);
    static DesktopControl Button(string key = "button-id", string name = "저장", string context = "문서") => new(key, "Button", name, context, "invoke", false, "", "revision-1");
    sealed class Driver : IDesktopControlDriver
    {
        internal DesktopControlSnapshot Snapshot = new("FixtureApp", "Fixture document", new[] { Field(), Button() });
        internal readonly List<string> Writes = new();
        internal int Reads;
        public Task<DesktopControlSnapshot> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(Snapshot); }
        void Apply(DesktopControl control, string value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Snapshot.Controls.Contains(control)) throw new AgentStoppedException("Fixture control changed during review");
            Writes.Add(control.Key + ":" + value);
        }
        public Task PressAsync(DesktopControl control, CancellationToken token) { Apply(control, "pressed", token); return Task.CompletedTask; }
        public Task FillAsync(DesktopControl control, string text, CancellationToken token) { Apply(control, text, token); return Task.CompletedTask; }
        public Task InsertAsync(DesktopControl control, string text, CancellationToken token) { Apply(control, text, token); return Task.CompletedTask; }
    }
    static async Task<AssistantAction> ObserveField(DesktopControls controls)
    {
        var page = await controls.ListAsync("본문", 0, CancellationToken.None);
        return new("fill_field", "새 한글 본문", Target: page.Controls.Single().Id);
    }
    internal static async Task<int> RunAsync(List<object> rows)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> run)
        {
            try { await run(); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
        }
        Task<bool> Approve(AssistantAction action, CancellationToken token) => Task.FromResult(true);
        Task<ActionOutcome> Done(AssistantAction action, CancellationToken token) => Task.FromResult(new ActionOutcome("done"));
        var root = Path.Combine(AppPaths.WindowsRoot, "artifacts", "screen-learning-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await Check("Screen learning captures semantic controls after real MCP lookup and multi-step execution", async () =>
        {
            var driver = new Driver(); var controls = new DesktopControls(driver); AssistantAction? field = null;
            var run = new McpAgentRun(Approve, async (action, token) =>
            {
                if (action.Kind == "list_controls") { field = await ObserveField(controls); return new("listed"); }
                var replay = controls.LearningAction(action); await controls.FillAsync(action.Target, action.Value, token);
                return new("filled", Replay: replay);
            }, null, null);
            await run.DispatchAsync(new("list_controls"), CancellationToken.None);
            await run.DispatchAsync(field!, CancellationToken.None);
            var result = run.Finish(new("finished", Array.Empty<AssistantAction>()));
            var learned = result.LearningPlan ?? throw new Exception("MCP second step was not learned");
            Require(learned.Actions.Single().Kind == "learned_fill" && !learned.Actions[0].Target.Contains("old-id") && !learned.Actions[0].Target.Contains(field!.Target) && !learned.Actions[0].Target.Contains("original"), "Transient IDs or field contents leaked into selector");
            var store = new Personalization(Path.Combine(root, "mcp-personal.json")); store.Learn("본문 채워 주세요", learned);
            var loaded = new Personalization(store.StoragePath); Require(loaded.Resolve("본문채워주세요")!.Actions.Single() == learned.Actions.Single(), "Restart or canonical phrase lost screen learning");
        });
        await Check("Screen learning resolves new IDs and reordered controls before each replayed step", async () =>
        {
            var driver = new Driver(); var controls = new DesktopControls(driver); var action = await ObserveField(controls);
            var stored = controls.LearningAction(action)!;
            driver.Snapshot = driver.Snapshot with { Controls = new[] { Button("new-button"), Field("new-field") } };
            var approvals = 0;
            var result = await ScreenLearning.ReplayAsync(new("custom confirmation", new[] { stored }), controls.PrepareReplayAsync,
                (current, token) => { approvals++; Require(controls.Describe(current).Contains("새 한글 본문"), "Actual target/text missing from approval"); return Task.FromResult(true); },
                async (current, token) => { await controls.FillAsync(current.Target, current.Value, token); return new("actual new result"); }, null, null, CancellationToken.None);
            Require(result.Finished && approvals == 1 && result.Reply == "custom confirmation" && result.Observations.Single().Summary == "actual new result" && driver.Writes.Single().StartsWith("new-field:") && result.LearningPlan == null, "Replay lost custom reply or actual observation, used old identity, skipped confirmation or relearned itself");
        });
        await Check("Screen learning blocks ambiguous missing changed-app and incomplete snapshots without input", async () =>
        {
            var driver = new Driver(); var controls = new DesktopControls(driver); var stored = controls.LearningAction(await ObserveField(controls))!;
            var original = driver.Snapshot;
            foreach (var snapshot in new[] { original with { Controls = new[] { Field("a"), Field("b") } }, original with { Controls = Array.Empty<DesktopControl>() }, original with { Application = "OtherApp" }, original with { Truncated = true }, original with { Controls = new[] { Field(context: "other section") } } })
            {
                driver.Snapshot = snapshot;
                var result = await ScreenLearning.ReplayAsync(new("", new[] { stored }), controls.PrepareReplayAsync, Approve, Done, null, null, CancellationToken.None);
                Require(!result.Finished && result.Observations.Single().Status == "blocked" && driver.Writes.Count == 0, "Changed or ambiguous target executed");
            }
        });
        await Check("Screen learning keeps approval-time revision and cursor guards in the shared driver", async () =>
        {
            var driver = new Driver(); var controls = new DesktopControls(driver); var stored = controls.LearningAction(await ObserveField(controls))!;
            var result = await ScreenLearning.ReplayAsync(new("", new[] { stored }), controls.PrepareReplayAsync,
                (current, token) => { driver.Snapshot = driver.Snapshot with { Controls = new[] { Field() with { Revision = "changed" } } }; return Task.FromResult(true); },
                async (current, token) => { await controls.FillAsync(current.Target, current.Value, token); return new("filled"); }, null, null, CancellationToken.None);
            Require(!result.Finished && driver.Writes.Count == 0, "Stale approved control was applied");
        });
        await Check("Screen learning declines without later actions and cancels before dispatch", async () =>
        {
            var typed = ScreenLearning.Input(new("type", "fixture"), false)!; var writes = 0;
            var plan = new CommandPlan("", new[] { typed, new AssistantAction("volume", Number: 20) });
            var declined = await ScreenLearning.ReplayAsync(plan, (a, t) => Task.FromResult(ScreenLearning.IsStored(a) ? ScreenLearning.RuntimeInput(a) : a), (_, _) => Task.FromResult(false),
                (a, t) => { writes++; return Done(a, t); }, null, null, CancellationToken.None);
            Require(declined.Declined && !declined.Finished && writes == 0, "Decline ran a later step");
            using var cancel = new CancellationTokenSource();
            await Reject(() => ScreenLearning.ReplayAsync(plan, (a, t) => { cancel.Cancel(); return Task.FromResult(ScreenLearning.RuntimeInput(a)); }, Approve,
                (a, t) => { writes++; return Done(a, t); }, null, null, cancel.Token));
            Require(writes == 0, "Canceled preparation dispatched input");
        });
        await Check("Screen learning stops after partial failure without retrying completed or dependent actions", async () =>
        {
            var calls = new List<string>();
            var actions = new[] { new AssistantAction("timer", "first", 10), ScreenLearning.Input(new("type", "second"), false)!, new AssistantAction("volume", Number: 40) };
            var result = await ScreenLearning.ReplayAsync(new("", actions), (a, t) => Task.FromResult(ScreenLearning.IsStored(a) ? ScreenLearning.RuntimeInput(a) : a), Approve,
                (a, t) => { calls.Add(a.Kind); if (a.Kind == "type") throw new InvalidOperationException("partial fixture input"); return Done(a, t); }, null, null, CancellationToken.None);
            Require(!result.Finished && calls.SequenceEqual(new[] { "timer", "type" }) && result.Observations.Count == 2 && result.LearningPlan == null, "Partial replay retried or ran a dependent action");
        });
        await Check("Screen learning uses safe permission even inside a full request and validates the entire sequence first", async () =>
        {
            using var full = ScreenAccess.Begin("full"); var prepared = 0;
            var valid = new CommandPlan("", new[] { ScreenLearning.Input(new("key", "ctrl+c"), false)! });
            var result = await ScreenLearning.ReplayAsync(valid, (a, t) => { prepared++; Require(!ScreenAccess.Full, "Preparation inherited full access"); return Task.FromResult(ScreenLearning.RuntimeInput(a)); }, Approve,
                (a, t) => { Require(!ScreenAccess.Full, "Dispatch inherited full access"); return Done(a, t); }, null, null, CancellationToken.None);
            Require(result.Finished && ScreenAccess.Full, "Safe replay leaked or revoked outer request");
            await Reject(() => ScreenLearning.ReplayAsync(new("", new[] { valid.Actions[0], new AssistantAction("learned_key", "alt+f4") }), (a, t) => { prepared++; return Task.FromResult(a); }, Approve, Done, null, null, CancellationToken.None));
            Require(prepared == 1, "Invalid later action allowed earlier preparation");
        });
        await Check("Screen learning excludes every original closing-key variant and Windows close equivalents", async () =>
        {
            foreach (var key in new[] { "cmd+w", "cmd+shift+w", "cmd+q", "cmd+option+w", "cmd+alt+w", "ctrl+w", "ctrl+shift+q", "alt+f4" })
                Require(ScreenLearning.Input(new("key", key), false) == null && !Personalization.CanReuse(new[] { new AssistantAction("learned_key", key) }), "Closing key learned: " + key);
            Require(ScreenLearning.Input(new("key", "cmd+c"), false) != null && ScreenLearning.Input(new("key", "option+left"), false) != null, "Ordinary original keys excluded");
            await Task.CompletedTask;
        });
        await Check("Screen learning never persists sensitive fields input or dependent screenshot coordinates", async () =>
        {
            using var full = ScreenAccess.Begin("full"); var driver = new Driver(); driver.Snapshot = driver.Snapshot with { Controls = new[] { Field(sensitive: true) } };
            var controls = new DesktopControls(driver); var field = await ObserveField(controls);
            Require(controls.LearningAction(field) == null && ScreenLearning.Input(new("type", "fixture-secret"), true) == null && ScreenLearning.Input(new("key", "enter"), true) == null, "Sensitive action became reusable");
            var run = new AgentRunResult("done", new[] { new ActionObservation(1, 1, field, "succeeded", "done", NoLearn: true) }, true);
            Require(run.LearningPlan == null, "Sensitive result entered learning");
            Require(new AgentRunResult("done", new[] { new ActionObservation(1, 1, new("click", Guid.NewGuid().ToString("N"), X: 10, Y: 10), "succeeded", "done") }, true).LearningPlan == null, "Coordinates learned");
        });
        await Check("Screen learning rejects malformed selectors hidden IDs unknown members and direct AI invocation", async () =>
        {
            var control = ScreenLearning.Control("learned_press", new("FixtureApp", "Button", "Save", "Document", "invoke", false, 0));
            foreach (var invalid in new[] { control with { Target = "{}" }, control with { Target = control.Target[..^1] + ",\"RuntimeId\":\"forged\"}" }, control with { Number = 1 }, control with { X = 2 }, control with { Value = "unrequested" } })
                Require(!Personalization.CanReuse(new[] { invalid }), "Malformed stored selector accepted");
            await Reject(() => { ActionPolicy.ValidateAi(control); return Task.CompletedTask; });
            var serialized = JsonSerializer.Serialize(new ActionObservation(1, 1, new("press_control", "id"), "succeeded", "done", Replay: control, NoLearn: true));
            Require(!serialized.Contains("FixtureApp") && !serialized.Contains("Replay") && !serialized.Contains("NoLearn"), "Private learning metadata exposed to model transport");
        });
        await Check("Screen learning imports original unit-separated type field tap and key steps with original metadata", () =>
        {
            var commands = new[] { "jax\u001fvolume\u001f30", "jax\u001ffield\u001fSearch\u001fhello world", "jtype\u001f and\u001fmore", "jax\u001ftap\u001fSave\u001f2", "jax\u001fkey\u001fcmd+c" };
            var source = MacPersonalTransfer.Parse(new Dictionary<string, string> { ["learned.json"] = JsonSerializer.Serialize(new Dictionary<string, object> { ["검색을 준비해줘"] = new { commands, uses = 9, learned = 12345.0 } }) });
            var plan = source.Package.Personal.Learned.Single().Value;
            Require(plan.Actions.Select(x => x.Kind).SequenceEqual(new[] { "volume", "learned_insert", "learned_type", "learned_press", "learned_key" }) && ScreenLearning.Selector(plan.Actions[3]).Occurrence == 2 && plan.Actions[2].Value == " and more" && source.Package.Personal.MacLearning.Single().Value == new MacLearningInfo(9, 12345), "Original Mac command arguments or metadata lost");
            var driver = new Driver(); var controls = new DesktopControls(driver); Require(driver.Reads == 0 && source.Notices.Any(x => x.Contains("제한 권한")), "Import executed or omitted platform notice");
            var text = JsonSerializer.Serialize(source.Package, SettingsStore.Json); var roundtrip = PersonalDataTransfer.Parse(text);
            Require(roundtrip.Personal.Learned.Single().Value.Actions.SequenceEqual(plan.Actions), "Portable screen learning did not roundtrip");
            return Task.CompletedTask;
        });
        await Check("Screen learning preserves exact contextual duplicate controls and explicit original nth-button selection", async () =>
        {
            var driver = new Driver { Snapshot = new("FixtureApp", "Document", new[] { Button("first", "Save", "Left"), Button("second", "Save", "Right") }) };
            var controls = new DesktopControls(driver);
            var stored = ScreenLearning.Control("learned_press", new("FixtureApp", "Button", "Save", "Right", "invoke", false, 0));
            var current = await controls.PrepareReplayAsync(stored, CancellationToken.None); await controls.PressAsync(current.Value, CancellationToken.None);
            var nth = ScreenLearning.Control("learned_press", new("", "", "Save", "", "", false, 2));
            current = await controls.PrepareReplayAsync(nth, CancellationToken.None); await controls.PressAsync(current.Value, CancellationToken.None);
            Require(driver.Writes.SequenceEqual(new[] { "second:pressed", "second:pressed" }), "Duplicate names selected wrong context or original nth control");
            driver.Snapshot = driver.Snapshot with { Controls = new[] { Button("combo", "Options") with { Role = "ComboBox", Operation = "expand_or_collapse", Editable = true } } };
            var page = await controls.ListAsync("Options", 0, CancellationToken.None);
            var combo = controls.LearningAction(new("press_control", page.Controls.Single().Id))!;
            current = await controls.PrepareReplayAsync(combo, CancellationToken.None); await controls.PressAsync(current.Value, CancellationToken.None);
            Require(driver.Writes.Last() == "combo:pressed", "Editable selection control could not replay its press operation");
        });
        await Check("Screen learning manual management saves displays and restores input while short or conditional automatic learning is excluded", () =>
        {
            var input = Personalization.ParseStep("안녕하세요 라고 써 줘").Actions.Single(); var key = Personalization.ParseStep("키 ctrl+c").Actions.Single(); var tap = Personalization.ParseStep("버튼 저장").Actions.Single();
            Require(input.Kind == "learned_type" && key.Kind == "learned_key" && tap.Kind == "learned_press" && Personalization.DescribeAction(input).Contains("안녕하세요") && Personalization.ParseStep("key ctrl+c").Actions.Single() == key && ScreenLearning.Selector(Personalization.ParseStep("button Save").Actions.Single()).Name == "Save", "Management cannot describe or create learned input");
            var path = Path.Combine(root, "manual-personal.json"); var store = new Personalization(path);
            store.SaveLearned("인사 입력", "done", new[] { input, key, tap }, null, store.Version);
            var previous = store.Snapshot(); store.SaveLearned("새 인사", "new reply", new[] { key }, "인사 입력", store.Version); store.Restore(previous, store.Version);
            Require(new Personalization(path).Resolve("인사입력")!.Actions.Count == 3 && File.Exists(path + ".backup"), "Edit/restore/restart lost screen steps");
            var count = store.Snapshot().Learned.Count; store.Learn("인사", new("", new[] { input })); store.Learn("본문을 확인해서 비었으면 인사해줘", new("", new[] { input }));
            Require(store.Snapshot().Learned.Count == count, "Short or conditional command automatically learned");
            return Task.CompletedTask;
        });
        return failed;
    }
}
