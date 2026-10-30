using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

// Persist descriptions of controls, never UIA runtime IDs, window handles or field values.
internal sealed record ReplayControl(string Application, string Role, string Name, string Context, string Operation, bool? Editable, int Occurrence);

internal static class ScreenLearning
{
    static readonly JsonSerializerOptions json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 8 };
    static readonly HashSet<string> lookups = new() { "list_controls", "front", "screenshot" };
    internal static bool IsControl(AssistantAction action) => action.Kind is "learned_press" or "learned_fill" or "learned_insert";
    internal static bool IsStored(AssistantAction action) => IsControl(action) || action.Kind is "learned_type" or "learned_key";
    internal static bool HasScreenSteps(IEnumerable<AssistantAction> actions) => actions.Any(IsStored);

    internal static void ValidateStored(AssistantAction action)
    {
        if (!IsStored(action) || action.Value == null || action.Target == null || action.Value.Length > 4000 || action.Number != 0 || action.X != 0 || action.Y != 0) throw new ArgumentException("저장한 화면 동작의 인수를 확인하세요.");
        if (!IsControl(action))
        {
            if (action.Target.Length != 0 || action.Value.Length == 0) throw new ArgumentException("저장한 입력 내용을 확인하세요.");
            if (action.Kind == "learned_key" && ClosingKey(KeyboardChord.Parse(action.Value))) throw new ArgumentException("창·탭·앱을 닫는 키는 학습하지 않아요.");
            ActionPolicy.Validate(action with { Kind = action.Kind == "learned_key" ? "key" : "type" });
            return;
        }
        if (action.Target.Length is < 1 or > 2600 || action.Kind == "learned_press" && action.Value.Length != 0 || action.Kind == "learned_insert" && action.Value.Length == 0) throw new ArgumentException("저장한 화면 항목이나 글을 확인하세요.");
        _ = Selector(action);
    }
    static bool ClosingKey(KeyboardChord chord) =>
        chord.Key == 0x73 && (chord.Modifiers & 1) != 0 ||
        chord.Key is 0x57 or 0x51 && (chord.Modifiers & 2) != 0;

    internal static ReplayControl Selector(AssistantAction action)
    {
        ReplayControl selector;
        try
        {
            using var document = JsonDocument.Parse(action.Target, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.RootElement.EnumerateObject().Count()) throw new JsonException();
            selector = JsonSerializer.Deserialize<ReplayControl>(action.Target, json) ?? throw new JsonException();
        }
        catch (JsonException) { throw new ArgumentException("저장한 화면 항목 설명이 올바르지 않아요."); }
        var texts = new[] { selector.Application, selector.Role, selector.Name, selector.Context, selector.Operation };
        if (texts.Any(x => x == null || x.Length > 500 || x.Contains('\0')) || selector.Role.Length > 40 || selector.Operation is not ("" or "invoke" or "toggle" or "select" or "expand_or_collapse") || selector.Occurrence is < 0 or > 120 || action.Kind != "learned_press" && selector.Editable != true || selector.Name.Length == 0 && selector.Occurrence == 0) throw new ArgumentException("저장한 화면 항목의 이름·종류·순서를 확인하세요.");
        return selector;
    }
    internal static AssistantAction Control(string kind, ReplayControl selector, string text = "")
    {
        var result = new AssistantAction(kind, text, Target: JsonSerializer.Serialize(selector)); ValidateStored(result); return result;
    }
    internal static AssistantAction? Input(AssistantAction action, bool sensitive)
    {
        if (sensitive || action.Kind is not ("type" or "key")) return null;
        var result = action with { Kind = action.Kind == "key" ? "learned_key" : "learned_type" };
        try { ValidateStored(result); return result; } catch (ArgumentException) { return null; }
    }
    internal static CommandPlan? Plan(AgentRunResult run)
    {
        if (!run.Finished || run.Observations.Count == 0 || run.Observations.Any(x => x.Status != "succeeded" || x.NoLearn)) return null;
        var actions = run.Observations.Where(x => !lookups.Contains(x.Action.Kind)).Select(x => x.Replay ?? x.Action).ToArray();
        // Match Learning.swift: only action-only turns, no coordinates, closing keys or dependent IDs.
        if (actions.Length is < 1 or > 6 || !Personalization.CanReuse(actions)) return null;
        return new(run.Reply, actions);
    }
    internal static AssistantAction RuntimeInput(AssistantAction action)
    {
        ValidateStored(action);
        return action.Kind switch { "learned_type" => action with { Kind = "type" }, "learned_key" => action with { Kind = "key" }, _ => throw new ArgumentException("이 화면 동작은 현재 항목을 다시 조회해야 해요.") };
    }
    internal static string Describe(AssistantAction action)
    {
        ValidateStored(action);
        if (action.Kind == "learned_type") return UiText.Format("글 입력 · {0}", action.Value);
        if (action.Kind == "learned_key") return UiText.Format("키 누르기 · {0}", KeyboardChord.Parse(action.Value).Label);
        var selector = Selector(action);
        var kind = action.Kind switch { "learned_press" => UiText.Get("버튼·메뉴 누르기"), "learned_fill" => UiText.Get("입력칸 전체 교체"), _ => UiText.Get("입력칸 커서에 추가") };
        var label = string.Join(" / ", new[] { selector.Application, selector.Context, selector.Name }.Where(x => x.Length > 0));
        return kind + " · " + label + (selector.Occurrence > 0 ? $" [{selector.Occurrence}]" : "") + (action.Kind == "learned_press" ? "" : " ← " + action.Value);
    }
    internal static async Task<AgentRunResult> ReplayAsync(CommandPlan plan,
        Func<AssistantAction, CancellationToken, Task<AssistantAction>> prepare,
        Func<AssistantAction, CancellationToken, Task<bool>> approve,
        Func<AssistantAction, CancellationToken, Task<ActionOutcome>> execute,
        Action<int, AssistantAction>? starting, Action<ActionObservation>? observed, CancellationToken token)
    {
        using var safe = ScreenAccess.Begin("safe", token);
        // Validate the complete stored sequence before the first observation or side effect.
        var actions = plan.Actions.ToArray();
        if (actions.Length is < 1 or > 8 || !Personalization.CanReuse(actions)) throw new ArgumentException("저장한 실행 방법을 관리 화면에서 다시 확인하세요.");
        foreach (var action in actions) { if (IsStored(action)) ValidateStored(action); else ActionPolicy.Validate(action); }
        var observations = new List<ActionObservation>();
        foreach (var stored in actions)
        {
            token.ThrowIfCancellationRequested(); var step = observations.Count + 1; var action = stored;
            try
            {
                action = await prepare(stored, token); token.ThrowIfCancellationRequested(); ActionPolicy.ValidateAi(action);
                if (ActionPolicy.NeedsConfirmation(action, true) && !await approve(action, token))
                {
                    token.ThrowIfCancellationRequested(); return new("저장한 실행 방법을 취소했어요. 앞서 실행한 동작은 기록을 확인하세요.", observations.ToArray(), false, true);
                }
                token.ThrowIfCancellationRequested(); starting?.Invoke(step, action);
                var result = await execute(action, token); token.ThrowIfCancellationRequested();
                var item = new ActionObservation(1, step, action, "succeeded", result.Summary, result.Data, NoLearn: true);
                observations.Add(item); observed?.Invoke(item);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var item = new ActionObservation(1, step, action, "blocked", ex.Message, NoLearn: true); observations.Add(item); observed?.Invoke(item);
                return new($"{step}번째 저장 동작에서 멈췄어요. 앞선 동작을 반복하지 않았어요. {ex.Message}", observations.ToArray(), false);
            }
        }
        return new(string.IsNullOrWhiteSpace(plan.Reply) ? UiText.Format("저장한 실행 방법대로 동작 {0}개를 처리했어요. 각 단계의 결과는 실행 기록에서 확인하세요.",observations.Count) : plan.Reply, observations.ToArray(), true);
    }
}
