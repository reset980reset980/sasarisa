using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed class McpAgentRun(
    Func<AssistantAction, CancellationToken, Task<bool>> approve,
    Func<AssistantAction, CancellationToken, Task<ActionOutcome>> execute,
    Action<int, AssistantAction>? starting, Action<ActionObservation>? observed)
{
    readonly List<ActionObservation> observations = new();
    bool stopped, declined;
    string stopReason = "";
    internal async Task<McpReply> DispatchAsync(AssistantAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stopped) return new(true, "blocked", stopReason);
        ActionPolicy.ValidateAi(action);
        var step = observations.Count + 1;
        if (step > AgentRun.MaxRounds * AgentRun.MaxActionsPerRound)
        { stopped = true; stopReason = "이번 요청의 실행 횟수 한도에 도달해 멈췄어요."; return new(true, "blocked", stopReason); }
        try
        {
            if (ActionPolicy.NeedsConfirmation(action, true) && !await approve(action, token))
            { token.ThrowIfCancellationRequested(); stopped = declined = true; stopReason = observations.Count == 0 ? "실행을 취소했어요." : "추가 실행을 취소했어요. 앞서 실행한 동작은 기록에 남아 있어요."; return new(true, "declined", stopReason); }
            token.ThrowIfCancellationRequested(); starting?.Invoke(step, action);
            var result = await execute(action, token);
            return Record("succeeded", result.Summary, result.Data) with { Image = result.Image };
        }
        catch (OperationCanceledException) { stopped = true; stopReason = "실행이 중단됐어요."; throw; }
        catch (AgentStoppedException ex) { stopped = true; stopReason = ex.Message; return Record("blocked", ex.Message); }
        catch (Exception ex) { return Record("failed", ex.Message); }

        McpReply Record(string status, string summary, string? data = null)
        {
            var item = new ActionObservation(step, step, action, status, summary, data);
            observations.Add(item); observed?.Invoke(item); return new(status != "succeeded", status, summary, data);
        }
    }
    internal AgentRunResult Finish(CommandPlan answer)
    {
        if (stopped) return new(stopReason, observations.ToArray(), false, declined);
        if (answer.Actions.Count != 0) return new("AI가 도구 실행 대신 계획만 반환했어요. 실행 기록을 확인하세요.", observations.ToArray(), false);
        if (observations.LastOrDefault()?.Status is "failed" or "blocked") return new(observations[^1].Summary, observations.ToArray(), false);
        return new(answer.Reply, observations.ToArray(), !string.IsNullOrWhiteSpace(answer.Reply));
    }
}
