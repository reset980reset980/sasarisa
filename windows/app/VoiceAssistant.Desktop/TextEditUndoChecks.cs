using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class TextEditUndoChecks
{
    internal static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> test) { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        void Require(bool yes) { if (!yes) throw new Exception("Undo invariant failed"); }
        await Check("Text undo restores selection, surrounding Korean text and emoji after caret movement", async () =>
        {
            var before = new FieldEditState("앞🎵 바꿀말\n뒤", 4, 3); var after = before.Apply("새 글"); var state = after with { Start = 0 };
            var undo = new TextEditUndo(before, after, _ => Task.FromResult(state), (original, expected, _) => { Require(state.Text == expected); state = original; return Task.CompletedTask; });
            await undo.RunAsync(default); Require(state == before);
        });
        await Check("Text undo rejects user changes without writing and preserves retry history", async () =>
        {
            var before = new FieldEditState("원문", 1, 0); var after = before.Apply("추가"); var state = after with { Text = "사용자가 수정" }; int writes = 0;
            var history = new ActionHistory(); var undo = new TextEditUndo(before, after, _ => Task.FromResult(state), (original, _, _) => { writes++; state = original; return Task.CompletedTask; });
            history.Record("입력", undo.RunAsync, documentEdit: true);
            try { await history.UndoAsync(default); throw new Exception("Accepted user edit"); } catch (AgentStoppedException) { }
            Require(writes == 0 && !history.Describe().Contains("되돌림")); state = after; await history.UndoAsync(default); await history.UndoAsync(default); Require(writes == 1);
        });
        await Check("Cancelled text undo and invalid state never reach restore", async () =>
        {
            int writes = 0; var state = new FieldEditState("🎵", 0, 0); using var stop = new CancellationTokenSource(); stop.Cancel();
            var undo = new TextEditUndo(state, state, _ => Task.FromResult(state), (_, _, _) => { writes++; return Task.CompletedTask; });
            try { await undo.RunAsync(stop.Token); throw new Exception("Accepted cancellation"); } catch (OperationCanceledException) { }
            undo = new(state with { Start = 1 }, state, _ => Task.FromResult(state), (_, _, _) => { writes++; return Task.CompletedTask; });
            try { await undo.RunAsync(default); throw new Exception("Accepted surrogate split"); } catch (InvalidOperationException) { }
            Require(writes == 0);
        });
        await Check("Failed or partial restoration is never reported as undone", async () =>
        {
            var before = new FieldEditState("원문", 0, 0); var after = before.Apply("추가"); var state = after;
            var history = new ActionHistory(); var undo = new TextEditUndo(before, after, _ => Task.FromResult(state), (_, _, _) => { state = state with { Text = "부분" }; return Task.CompletedTask; }); history.Record("입력", undo.RunAsync);
            try { await history.UndoAsync(default); throw new Exception("Accepted mismatch"); } catch (InvalidOperationException) { }
            Require(!history.Describe().Contains("되돌림"));
        });
        await Check("Dictation undo never skips unsupported edits or reverses a timer", async () =>
        {
            int writes = 0; var history = new ActionHistory(); history.Record("입력", _ => { writes++; return Task.FromResult("restored"); }, documentEdit: true);
            history.Record("지원하지 않는 입력", unavailable: "manual", documentEdit: true); Require(await history.UndoDocumentAsync(default) == "지원하지 않는 입력 · manual");
            history.Record("타이머", _ => { writes++; return Task.FromResult("timer removed"); }); await history.UndoDocumentAsync(default); Require(writes == 0);
        });
        return failed;
    }
}
