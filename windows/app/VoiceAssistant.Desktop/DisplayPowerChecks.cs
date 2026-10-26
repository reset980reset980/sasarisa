using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class DisplayPowerChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    internal static async Task<int> RunAsync(List<object> rows)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test)
        { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Display requests distinguish monitor off from negation, shutdown, closing and quoted text", () =>
        {
            foreach (var text in new[] { "화면 꺼", "화면을 꺼 주세요", "모니터 꺼줘", "디스플레이를 끄세요", "Turn off the screen", "monitor off" })
                Require(CommandParser.Parse(text)?.Actions.Single().Kind == "display_off", text);
            foreach (var text in new[] { "화면 끄지 마", "컴퓨터 꺼", "화면 잠가", "화면 꺼라고 써 줘", "화면 꺼도 돼?", "모니터 설정 열어", "화면 꺼 그리고 메모장 열어" })
                Require(CommandParser.Parse(text)?.Actions.Any(a => a.Kind == "display_off") != true, "Misinterpreted: " + text);
            var sequence = Personalization.SplitCommands("소리 줄이고 화면 꺼").Select(x => CommandParser.Parse(x)!).SelectMany(x => x.Actions).ToArray();
            Require(sequence.Select(a => a.Kind).SequenceEqual(new[] { "volume_delta", "display_off" }) && Personalization.CanReuse(sequence), "Routine cannot retain display action/order");
            return Task.CompletedTask;
        });
        await Check("Display tool has no shell, parameters or unreviewed AI execution", async () =>
        {
            var action = McpTools.Decode("display_off", null); ActionPolicy.ValidateAi(action);
            Require(ActionPolicy.NeedsConfirmation(action, true) && !ActionPolicy.NeedsConfirmation(action, false), "Approval boundary changed");
            foreach (var malformed in new[] { action with { Value = "shutdown" }, action with { Number = -1 }, action with { Target = "other" }, action with { X = 1 } })
                await Reject<InvalidOperationException>(() => { ActionPolicy.Validate(malformed); return Task.CompletedTask; });
            await Reject<ArgumentException>(() => { McpTools.Decode("display_off", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"value\":\"shutdown\"}")); return Task.CompletedTask; });
            var calls = 0;
            var result = await AgentRun.RunAsync((_, _) => Task.FromResult(new CommandPlan("", [action])), (_, _) => Task.FromResult(false),
                (_, _) => { calls++; return Task.FromResult(new ActionOutcome("unexpected")); }, null, null, default);
            Require(result.Declined && calls == 0, "Declined action reached transport");
        });
        await Check("Display MCP approval decline stops later calls and approved execution preserves OS evidence", async () =>
        {
            var calls = 0;
            var declined = new McpAgentRun((_, _) => Task.FromResult(false), (_, _) => { calls++; return Task.FromResult(new ActionOutcome("unexpected")); }, null, null);
            Require((await declined.DispatchAsync(new("display_off"), default)).Status == "declined", "Approval bypassed");
            Require((await declined.DispatchAsync(new("display_off"), default)).Status == "blocked" && calls == 0, "Decline retried");
            var result = declined.Finish(new("done", [])); Require(result.Declined && !result.Finished && result.Observations.Count == 0, "Decline became success");
            var accepted = new McpAgentRun((_, _) => Task.FromResult(true), (_, _) => { calls++; return Task.FromResult(new ActionOutcome(new DisplayPowerResult(false).Summary)); }, null, null);
            await accepted.DispatchAsync(new("display_off"), default);
            var completed = accepted.Finish(new("requested", []));
            Require(calls == 1 && completed.Observations.Single().Summary.Contains("알림은 받지 못"), "Unconfirmed result lost");
        });
        await Check("Display request cancellation before quiet delay never reaches Windows", async () =>
        {
            var calls = 0;
            using var power = new DisplayPower(_ => { calls++; return Task.CompletedTask; }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
            using var cancellation = new CancellationTokenSource();
            var pending = power.OffAsync(cancellation.Token); cancellation.Cancel();
            await Reject<OperationCanceledException>(() => pending);
            Require(calls == 0, "Cancelled request sent");
        });
        await Check("Display delivery without OS off notification is not reported as confirmed", async () =>
        {
            var calls = 0;
            using var power = new DisplayPower(_ => { calls++; return Task.CompletedTask; }, TimeSpan.Zero, TimeSpan.FromMilliseconds(15));
            power.Observe(0); // Old state must not confirm a new request.
            var result = await power.OffAsync(default);
            Require(calls == 1 && !result.Confirmed && result.Summary.Contains("알림은 받지 못"), "Delivery or old state overclaimed");
        });
        await Check("Display only confirms a fresh off event and does not repeat the request", async () =>
        {
            DisplayPower? power = null; var calls = 0;
            using (power = new DisplayPower(_ => { calls++; power!.Observe(2); power.Observe(1); power.Observe(0); return Task.CompletedTask; }, TimeSpan.Zero, TimeSpan.FromMilliseconds(15)))
            {
                Require((await power.OffAsync(default)).Confirmed && power.ObservedState == 0 && calls == 1, "Off event missing");
                power.Observe(1); Require(power.ObservedState == 1 && calls == 1, "Wake caused another off request");
            }
        });
        await Check("Display transport failure, cancellation after delivery, and disposal do not retry", async () =>
        {
            using var broken = new DisplayPower(_ => throw new InvalidOperationException("native failed"), TimeSpan.Zero, TimeSpan.Zero);
            await Reject<InvalidOperationException>(() => broken.OffAsync(default));
            using var cancellation = new CancellationTokenSource(); var calls = 0;
            using var power = new DisplayPower(_ => { calls++; cancellation.Cancel(); return Task.CompletedTask; }, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            await Reject<OperationCanceledException>(() => power.OffAsync(cancellation.Token));
            Require(calls == 1, "Request retried after cancellation");
            power.Dispose(); await Reject<ObjectDisposedException>(() => power.OffAsync(default));
            Require(calls == 1, "Disposed transport called");
        });
        await Check("Display notification accepts only session-display DWORD states", () =>
        {
            var data = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.StructureToPtr(DisplayPower.SessionDisplay, data, false); Marshal.WriteInt32(data, 16, 4);
                foreach (var state in new[] { 0, 1, 2 }) { Marshal.WriteInt32(data, 20, state); Require(DisplayPower.ReadNotification(0x218, (IntPtr)0x8013, data) == state, "OS state rejected"); }
                Marshal.WriteInt32(data, 20, 9); Require(DisplayPower.ReadNotification(0x218, (IntPtr)0x8013, data) == null, "Unknown state accepted");
                Marshal.WriteInt32(data, 20, 0); Marshal.WriteInt32(data, 16, 3); Require(DisplayPower.ReadNotification(0x218, (IntPtr)0x8013, data) == null, "Wrong length accepted");
                Marshal.WriteInt32(data, 16, 4); Marshal.StructureToPtr(Guid.NewGuid(), data, false); Require(DisplayPower.ReadNotification(0x218, (IntPtr)0x8013, data) == null, "Unrelated power event accepted");
                Require(DisplayPower.ReadNotification(0x218, (IntPtr)0x8013, IntPtr.Zero) == null, "Null message accepted");
            }
            finally { Marshal.FreeHGlobal(data); }
            return Task.CompletedTask;
        });
        return failed;
    }
}
