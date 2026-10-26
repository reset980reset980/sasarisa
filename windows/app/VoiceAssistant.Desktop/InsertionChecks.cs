using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class InsertionChecks
{
    static void Require(bool yes, string message) { if (!yes) throw new Exception(message); }
    static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OperationCanceledException) { return; } throw new Exception("Unexpected write accepted"); }
    sealed class Driver : IDesktopControlDriver
    {
        internal DesktopControl[] Items = [new("first", "Edit", "메시지", "첫 번째 영역", "", true, "", "first-text", 2), new("second", "Edit", "메시지", "두 번째 영역", "", true, "", "second-text", 3, 2)];
        internal string Inserted = "", Target = ""; internal bool Fail;
        public Task<DesktopControlSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(new DesktopControlSnapshot("fixture", "insertion", Items));
        public Task PressAsync(DesktopControl item, CancellationToken token) => throw new Exception("Unexpected press");
        public Task FillAsync(DesktopControl item, string text, CancellationToken token) => throw new Exception("Whole field replacement must not be used for insertion");
        public Task InsertAsync(DesktopControl item, string text, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (Fail || !Items.Contains(item)) throw new AgentStoppedException("Field changed"); Target = item.Key; Inserted = text; return Task.CompletedTask; }
    }
    internal static async Task<int> RunAsync(List<object> rows, bool claude, bool codex)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test) { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Field insertion preserves target through JSON and MCP and always requests confirmation", async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { reply = "진행", actions = new[] { new { kind = "insert_field", target = id, value = "한글 🎵", number = 0 } } }));
            var action = Brain.Decode(json.RootElement).Actions.Single(); ActionPolicy.ValidateAi(action);
            Require(action.Target == id && ActionPolicy.NeedsConfirmation(action, false) && ActionPolicy.NeedsConfirmation(action, true), "Insertion lost target or approval");
            var decoded = McpTools.Decode("insert_field", new Dictionary<string, JsonElement> { ["target"] = JsonSerializer.SerializeToElement(id), ["value"] = JsonSerializer.SerializeToElement("한글 🎵") });
            Require(decoded == action, "MCP insertion differs from JSON");
            foreach (var invalid in new[] { action with { Target = "" }, action with { Value = "" }, action with { X = 1 }, action with { Number = 1 }, action with { Kind = "type" } }) await Reject(() => { ActionPolicy.ValidateAi(invalid); return Task.CompletedTask; });
        });
        await Check("Field insertion preserves surrounding text, selection, Hangul, emoji and line breaks", async () =>
        {
            foreach (var initial in new[] { new FieldEditState("앞글 뒤글", 3, 0), new FieldEditState("앞글 바꿀말 뒤글", 3, 3) })
            {
                var state = initial; var inserted = "한글🎵\r\n다음 줄";
                await FieldInsertion.RunAsync(initial, inserted, () => state, part => state = state.Apply(part), default, _ => Task.CompletedTask);
                Require(state.Text == "앞글 한글🎵\n다음 줄" + initial.Text[(initial.Start + initial.Length)..], "Surrounding text was lost");
            }
            var longState = new FieldEditState("보존", 1, 0); var chunks = new List<string>(); var longText = new string('가', 63) + "🎵" + new string('나', 80);
            await FieldInsertion.RunAsync(longState, longText, () => longState, part => { chunks.Add(part); longState = longState.Apply(part); }, default, _ => Task.CompletedTask);
            Require(longState.Text == "보" + longText + "존" && chunks.All(x => !char.IsHighSurrogate(x[^1]) && !char.IsLowSurrogate(x[0])), "UTF16 pair split or suffix lost");
        });
        await Check("Changed field content or caret before insertion produces no input", async () =>
        {
            var original = new FieldEditState("가나다", 1, 0); var writes = 0;
            foreach (var changed in new[] { original with { Text = "가너다" }, original with { Start = 2 }, original with { Length = 1 } }) await Reject(() => FieldInsertion.RunAsync(original, "추가", () => changed, _ => writes++, default));
            await Reject(() => FieldInsertion.RunAsync(new("🎵", 1, 0), "추가", () => original, _ => writes++, default));
            Require(writes == 0, "Stale insertion wrote text");
        });
        await Check("Caret changes and cancellation during insertion stop remaining chunks", async () =>
        {
            var original = new FieldEditState("앞뒤", 1, 0); var state = original; var writes = 0;
            await Reject(() => FieldInsertion.RunAsync(original, new string('한', 130), () => state, part => { writes++; state = state.Apply(part); }, default, _ => { state = state with { Start = 0 }; return Task.CompletedTask; }));
            Require(writes == 1 && state.Text == "앞" + new string('한', 64) + "뒤", "Continued after caret changed");
            using var cancellation = new CancellationTokenSource(); state = original; writes = 0;
            await Reject(() => FieldInsertion.RunAsync(original, new string('한', 130), () => state, part => { writes++; state = state.Apply(part); cancellation.Cancel(); }, cancellation.Token, _ => Task.CompletedTask));
            Require(writes == 1, "Continued after cancellation");
        });
        await Check("Insertion rejects oversized result before writing and reports partial mismatch", async () =>
        {
            var original = new FieldEditState(new string('가', 6000), 1, 0); var writes = 0;
            await Reject(() => FieldInsertion.RunAsync(original, "추가", () => original, _ => writes++, default)); Require(writes == 0, "Oversized result was written");
            var state = new FieldEditState("앞뒤", 1, 0);
            await Reject(() => FieldInsertion.RunAsync(state, "추가", () => state, _ => { writes++; state = state with { Text = "앱에서 다른 글로 변경" }; }, default, _ => Task.CompletedTask)); Require(writes == 1, "Mismatch was not checked after actual write");
        });
        await Check("Observed insertion IDs expire, require a cursor, and are consumed after success or failure", async () =>
        {
            var driver = new Driver(); var time = DateTimeOffset.UtcNow; var controls = new DesktopControls(driver, () => time);
            var page = await controls.ListAsync("", 0, default); var id = page.Controls[1].Id;
            var description = controls.Describe(new("insert_field", "추가", Target: id)); Require(description.Contains("선택된 부분만") && page.Controls[1].SelectionStart == 3, "Selection metadata or confirmation missing");
            await controls.InsertAsync(id, "추가", default); Require(driver.Target == "second", "Wrong same-label field"); await Reject(() => controls.InsertAsync(id, "again", default));
            page = await controls.ListAsync("", 0, default); time = time.AddMinutes(2); await Reject(() => controls.InsertAsync(page.Controls[0].Id, "late", default));
            driver.Items[0] = driver.Items[0] with { SelectionStart = null }; page = await controls.ListAsync("", 0, default); await Reject(() => controls.InsertAsync(page.Controls[0].Id, "unknown", default));
            driver.Fail = true; await Reject(() => controls.InsertAsync(page.Controls[1].Id, "failed", default)); driver.Fail = false; await Reject(() => controls.InsertAsync(page.Controls[1].Id, "retry stale", default));
        });
        await Check("Declined insertion never reaches a field driver", async () =>
        {
            var driver = new Driver(); var controls = new DesktopControls(driver); var page = await controls.ListAsync("", 0, default);
            var run = new McpAgentRun((_, _) => Task.FromResult(false), async (action, token) => { await controls.InsertAsync(action.Target, action.Value, token); return new("written"); }, null, null);
            var reply = await run.DispatchAsync(new("insert_field", "추가", Target: page.Controls[1].Id), default);
            Require(reply.Status == "declined" && driver.Inserted == "", "Declined insertion changed a field");
        });
        foreach (var (provider, enabled) in new[] { ("claude", claude), ("codex", codex) }.Where(p => p.Item2))
            await Check("Actual " + provider + " chooses field insertion rather than replacement using observed ID", async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125)); var driver = new Driver(); var controls = new DesktopControls(driver);
                var run = new McpAgentRun((_, _) => Task.FromResult(true), async (action, token) =>
                {
                    if (action.Kind == "front") return new ActionOutcome("현재 입력 시험 창", "{\"Application\":\"fixture\",\"Window\":\"insertion\",\"CanContinue\":true}");
                    if (action.Kind == "list_controls") return new ActionOutcome("항목 목록", ControlObservation.Data(await controls.ListAsync(action.Value, action.Number, token)));
                    if (action.Kind != "insert_field") throw new AgentStoppedException("Unexpected action " + action.Kind);
                    await controls.InsertAsync(action.Target, action.Value, token); return new("현재 선택 부분에 입력했고 주변 글은 보존됨");
                }, null, null);
                await using var session = new McpSession(run.DispatchAsync, timeout.Token);
                var answer = await new Brain().AskAsync("현재 창 두 번째 영역의 메시지 입력칸에 '윈도우 추가 확인'을 현재 선택 위치에 넣어 줘. 나머지 글과 첫 번째 영역은 그대로 두고 전체 교체하지 마. 먼저 목록에서 입력칸 ID와 선택 위치를 확인해.", "", new UiSettings { BrainProvider = provider }, timeout.Token, mcp: session.Endpoint);
                Require(run.Finish(answer).Finished && driver.Target == "second" && driver.Inserted == "윈도우 추가 확인", "Insertion chain incomplete: " + answer.Reply);
            });
        return failed;
    }
}
