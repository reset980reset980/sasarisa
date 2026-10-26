using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class KeyboardChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException) { return; } throw new Exception("Invalid keyboard action accepted"); }
    internal static async Task<int> RunAsync(List<object> rows, bool claude, bool codex)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test) { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Keyboard aliases normalize Mac modifiers and Windows key names without accepting sequences", () =>
        {
            Require(KeyboardChord.Parse(" Command + Shift + Left ").Label == "ctrl+shift+left", "Mac command alias not translated");
            Require(KeyboardChord.Parse("option+RETURN").Label == "alt+enter" && KeyboardChord.Parse("CTRL+Del").Key == 0x2E && KeyboardChord.Parse("back").Key == 8, "Key aliases wrong");
            foreach (var name in new[] { "a", "z", "0", "9", "f1", "f24", "space", "insert", "home", "end", "pageup", "pagedown", "numpadenter", "ctrl+shift+n", "alt+f4", "win+d", "plus", "ctrl+/" }) _ = KeyboardChord.Parse(name);
            foreach (var bad in new[] { "", " ", "ctrl", "ctrl++", "ctrl+cmd+a", "alt+option+x", "a+b", "f25", "ctrl+alt+del", "shift+ctrl+alt+forwarddelete", "a\nb", "한", "ctrl+a,ctrl+c", "ctrl+shift+alt+win+a+b" }) Reject(() => KeyboardChord.Parse(bad));
            return Task.CompletedTask;
        });
        await Check("Keyboard JSON and MCP retain compound keys and enforce one combination per action", () =>
        {
            using var json = JsonDocument.Parse("{\"reply\":\"선택\",\"actions\":[{\"kind\":\"key\",\"value\":\"ctrl+shift+left\",\"number\":0,\"target\":\"\"}]}");
            var action = Brain.Decode(json.RootElement).Actions.Single(); ActionPolicy.ValidateAi(action);
            Require(McpTools.Decode("key", new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("ctrl+shift+left") }) == action, "MCP/JSON key discrepancy");
            Reject(() => ActionPolicy.ValidateAi(new("key", "left", 3))); Reject(() => ActionPolicy.ValidateAi(new("key", "left", Target: "other"))); Reject(() => ActionPolicy.ValidateAi(new("key", "left", X: 1)));
            return Task.CompletedTask;
        });
        await Check("Editing and app shortcuts request confirmation while selection and legacy shortcuts remain direct", () =>
        {
            foreach (var key in new[] { "enter", "shift+enter", "ctrl+enter", "space", "delete", "shift+delete", "backspace", "ctrl+x", "ctrl+shift+n", "alt+f4", "ctrl+shift+escape", "win+d", "plus", "a" }) Require(ActionPolicy.NeedsConfirmation(new("key", key), true), "Missing confirmation: " + key);
            foreach (var key in new[] { "home", "end", "left", "right", "ctrl+shift+left", "shift+end", "pageup", "ctrl+home", "tab", "shift+tab", "escape", "ctrl+a", "ctrl+c", "ctrl+v", "ctrl+z" }) Require(!ActionPolicy.NeedsConfirmation(new("key", key), true), "Navigation unexpectedly changed: " + key);
            return Task.CompletedTask;
        });
        await Check("Keyboard event packets preserve extended flags and release modifiers in reverse order", () =>
        {
            var events = KeyboardDispatch.Sequence(KeyboardChord.Parse("ctrl+alt+shift+left"));
            Require(events.Select(x => x.Key).SequenceEqual(new ushort[] { 0x11, 0x12, 0x10, 0x25, 0x25, 0x10, 0x12, 0x11 }), "Wrong modifier ordering");
            Require(events[3].Extended && events[4].Extended && !events[3].Up && events.Skip(4).All(x => x.Up), "Extended key/release flags wrong");
            Require(KeyboardDispatch.Sequence(KeyboardChord.Parse("numpadenter")).All(x => x.Extended), "Keypad Enter lost extended flag");
            Require(KeyboardDispatch.Sequence(KeyboardChord.Parse("win+d")).Where(x => x.Key == 0x5B).All(x => x.Extended), "Windows modifier lost extended flag");
            return Task.CompletedTask;
        });
        await Check("Symbols resolve using target layout including Shift and AltGr and reject missing mappings", () =>
        {
            var plus = KeyboardChord.Parse("ctrl+plus").Resolve(_ => 0x01BB); Require(plus.Key == 0xBB && plus.Modifiers == 6 && plus.Symbol == null, "Shift symbol mapping lost");
            var altgr = KeyboardChord.Parse("/").Resolve(_ => 0x0637); Require(altgr.Key == 0x37 && altgr.Modifiers == 3, "AltGr mapping wrong");
            Reject(() => KeyboardChord.Parse("plus").Resolve(_ => -1)); Reject(() => KeyboardChord.Parse("plus").Resolve(_ => 0x0837));
            Require(KeyboardChord.Parse("command+shift+space").Matches(HotkeySpec.Parse("Ctrl+Shift+Space")), "Wake conflict not detected");
            Require(KeyboardChord.Parse("ctrl+alt+f10").Matches(HotkeySpec.Parse("Ctrl+Alt+F10")), "Fallback stop conflict not detected");
            return Task.CompletedTask;
        });
        await Check("Held keys, changed target and cancellation before delivery produce no keyboard events", () =>
        {
            var sent = 0; uint Send(IReadOnlyList<KeyStroke> input) { sent++; return (uint)input.Count; }
            foreach (var held in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x25 }) Reject(() => KeyboardDispatch.Send(KeyboardChord.Parse("left"), Send, key => key == held, () => { }, default));
            var guards = 0; Reject(() => KeyboardDispatch.Send(KeyboardChord.Parse("ctrl+x"), Send, _ => false, () => { if (++guards == 2) throw new AgentStoppedException("changed"); }, default));
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); Reject(() => KeyboardDispatch.Send(KeyboardChord.Parse("enter"), Send, _ => false, () => { }, canceled.Token));
            Require(sent == 0, "Unsafe input reached backend"); return Task.CompletedTask;
        });
        await Check("Every partial delivery prefix releases only delivered key-down events without repeating input", () =>
        {
            var chord = KeyboardChord.Parse("ctrl+alt+shift+f8"); var sequence = KeyboardDispatch.Sequence(chord);
            for (var prefix = 0; prefix < sequence.Length; prefix++)
            {
                var count = prefix; var calls = 0; var pressed = new HashSet<ushort>(); var downs = 0;
                uint Send(IReadOnlyList<KeyStroke> input)
                {
                    calls++; var accepted = calls == 1 ? count : input.Count;
                    if (calls > 1) Require(input.All(x => x.Up), "Cleanup repeated key-down input");
                    foreach (var item in input.Take(accepted)) { if (item.Up) pressed.Remove(item.Key); else { pressed.Add(item.Key); downs++; } }
                    return (uint)accepted;
                }
                Reject(() => KeyboardDispatch.Send(chord, Send, _ => false, () => { }, default));
                Require(pressed.Count == 0 && downs == sequence.Take(prefix).Count(x => !x.Up) && calls == (prefix == 0 ? 1 : 2), "Partial prefix left keys held or repeated input");
            }
            return Task.CompletedTask;
        });
        await Check("Cleanup failure is reported as blocked and declined keyboard commands never execute", async () =>
        {
            var writes = 0;
            var run = new McpAgentRun((_, _) => Task.FromResult(true), (action, token) =>
            {
                KeyboardDispatch.Send(KeyboardChord.Parse(action.Value), _ => { writes++; return writes == 1 ? 3u : 1u; }, _ => false, () => { }, token); return Task.FromResult(new ActionOutcome("sent"));
            }, null, null);
            var blocked = await run.DispatchAsync(new("key", "ctrl+shift+x"), default); Require(blocked.Status == "blocked" && writes == 2, "Partial delivery was retriable or cleanup missing");
            var calls = 0; var declined = new McpAgentRun((_, _) => Task.FromResult(false), (_, _) => { calls++; return Task.FromResult(new ActionOutcome("sent")); }, null, null);
            Require((await declined.DispatchAsync(new("key", "backspace"), default)).Status == "declined" && calls == 0, "Declined key executed");
        });
        foreach (var (provider, enabled) in new[] { ("claude", claude), ("codex", codex) }.Where(x => x.Item2))
            await Check("Actual " + provider + " performs ordered selection keys and confirms the edit", async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125)); var delivered = new List<string>(); var confirmed = new List<string>();
                var run = new McpAgentRun((action, _) => { confirmed.Add(KeyboardChord.Parse(action.Value).Label); return Task.FromResult(true); }, (action, _) =>
                {
                    if (action.Kind == "front") return Task.FromResult(new ActionOutcome("현재 시험 창", "{\"Application\":\"fixture\",\"Window\":\"키 시험\",\"CanContinue\":true}"));
                    if (action.Kind != "key") throw new AgentStoppedException("Unexpected action " + action.Kind);
                    delivered.Add(KeyboardChord.Parse(action.Value).Label); return Task.FromResult(new ActionOutcome("키 전달 확인", "{\"CanContinue\":true}"));
                }, null, null);
                await using var session = new McpSession(run.DispatchAsync, timeout.Token);
                var answer = await new Brain().AskAsync("이미 선택한 시험 창의 입력칸에서 Home, Shift+End, Backspace를 이 순서대로 한 번씩 눌러 현재 줄을 지워 줘. key 도구만 사용하고 전체 교체나 다른 창 전환은 하지 마. 각 키를 보낸 뒤 실제 결과와 한계를 짧게 알려줘.", "현재 시험 창 입력칸에 커서가 있어요.", new UiSettings { BrainProvider = provider }, timeout.Token, mcp: session.Endpoint);
                Require(run.Finish(answer).Finished && delivered.SequenceEqual(new[] { "home", "shift+end", "backspace" }) && confirmed.SequenceEqual(new[] { "backspace" }), "Wrong keyboard chain: " + string.Join(',', delivered) + " / " + answer.Reply);
            });
        return failed;
    }
}
