using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record ActionOutcome(string Summary, string? Data = null, AssistantImage? Image = null);
public sealed record ActionObservation(int Round, int Step, AssistantAction Action, string Status, string Summary, string? Data = null);
public sealed record AgentRunResult(string Reply, IReadOnlyList<ActionObservation> Observations, bool Finished, bool Declined = false)
{
    // Conditional plans must be evaluated again next time, not cached as a routine.
    public CommandPlan? LearningPlan => Finished && Observations.Count > 0 && Observations.All(x => x.Round == 1 && x.Status == "succeeded") && Personalization.CanReuse(Observations.Select(x => x.Action))
        ? new(Reply, Observations.Select(x => x.Action).ToArray()) : null;
}
public sealed class AgentStoppedException(string message) : InvalidOperationException(message);

public static class AgentRun
{
    public const int MaxRounds = 12;
    public const int MaxActionsPerRound = 6;
    public static async Task<AgentRunResult> RunAsync(
        Func<IReadOnlyList<ActionObservation>, CancellationToken, Task<CommandPlan>> plan,
        Func<AssistantAction, CancellationToken, Task<bool>> approve,
        Func<AssistantAction, CancellationToken, Task<ActionOutcome>> execute,
        Action<int, AssistantAction>? starting,
        Action<ActionObservation>? observed,
        CancellationToken token)
    {
        var results = new List<ActionObservation>();
        for (var round = 1; round <= MaxRounds; round++)
        {
            token.ThrowIfCancellationRequested();
            var next = await plan(results.ToArray(), token);
            token.ThrowIfCancellationRequested();
            if (next.Actions.Count > MaxActionsPerRound) throw new InvalidOperationException("한 단계의 동작이 너무 많아 실행하지 않았어요.");
            // Validate the entire proposed batch before its first side effect.
            foreach (var action in next.Actions) ActionPolicy.ValidateAi(action);
            if (next.Actions.Count == 0 || next.Actions.All(x => x.Kind == "answer"))
                return new(string.IsNullOrWhiteSpace(next.Reply) ? "AI가 최종 답변을 보내지 않았어요. 위 실행 결과를 확인하세요." : next.Reply, results.ToArray(), !string.IsNullOrWhiteSpace(next.Reply));
            foreach (var action in next.Actions)
            {
                token.ThrowIfCancellationRequested();
                var step = results.Count + 1;
                try
                {
                    if (action.Kind == "answer") continue;
                    if (ActionPolicy.NeedsConfirmation(action, true))
                    {
                        var approved = await approve(action, token); token.ThrowIfCancellationRequested();
                        if (!approved) return new(results.Count == 0 ? "실행을 취소했어요." : "추가 실행을 취소했어요. 앞서 실행한 동작은 위 기록에 남아 있어요.", results.ToArray(), false, true);
                    }
                    token.ThrowIfCancellationRequested(); starting?.Invoke(step, action);
                    var outcome = await execute(action, token);
                    var observation = new ActionObservation(round, step, action, "succeeded", outcome.Summary, outcome.Data);
                    results.Add(observation); observed?.Invoke(observation);
                }
                catch (OperationCanceledException) { throw; }
                catch (AgentStoppedException ex)
                {
                    var observation = new ActionObservation(round, step, action, "blocked", ex.Message);
                    results.Add(observation); observed?.Invoke(observation);
                    return new(ex.Message, results.ToArray(), false);
                }
                catch (Exception ex)
                {
                    var observation = new ActionObservation(round, step, action, "failed", ex.Message);
                    results.Add(observation); observed?.Invoke(observation);
                    // Later actions can depend on this one. Ask again with the actual failure.
                    break;
                }
            }
        }
        return new("12단계까지 진행했지만 요청을 마치지 못해 멈췄어요. 위 실행 결과를 확인하고 남은 작업을 알려 주세요.", results.ToArray(), false);
    }
    public static string Describe(AssistantAction action) => action.Kind switch
    {
        "kakao_chats" => "열린 카카오톡 대화방 확인",
        "kakao_draft" => "카카오톡 초안 입력 확인",
        "kakao_send" or "kakao_send_draft" => "카카오톡 수신 대화방과 전송 내용 확인",
        "list_sessions" => "Claude 기존 세션 목록 확인",
        "switch_session" => "확인한 Claude 세션으로 전환",
        "delegate_session" => "Claude 기존 세션에 작업 전달 · " + action.Value,
        "open_app" => "앱 열기 · " + (AppRegistry.Default.Find(action.Value)?.Name ?? CommandParser.Apps.FirstOrDefault(x => x.Value == action.Value).Key ?? action.Value),
        "open_url" => "웹 페이지 열기 · " + action.Value,
        "type" => "글 입력 · " + action.Value,
        "key" => "키 누르기 · " + KeyboardChord.Parse(action.Value).Label,
        "tap" => "버튼 누르기 · " + action.Value,
        "list_controls" => "화면의 버튼과 입력칸 확인",
        "list_windows" => "열린 앱 창 목록 확인",
        "select_window" => "목록에서 확인한 앱 창으로 전환",
        "front" => "현재 작업 창과 브라우저 주소 확인",
        "press_control" => "확인한 화면 항목 누르기",
        "fill_field" => "확인한 입력칸의 글 교체 · " + action.Value,
        "insert_field" => "확인한 입력칸의 커서·선택 영역에 글 입력 · " + action.Value,
        "screenshot" => "선택한 창의 이미지를 현재 AI에 전달",
        "click" => $"화면 이미지의 표시된 위치 누르기 · ({action.X}, {action.Y})",
        "browser" => "브라우저 이동 · " + action.Value,
        "browser_tabs" => action.Value == "all" ? "모든 브라우저 창의 탭 확인" : "브라우저 탭 확인",
        "browser_select" => "목록에서 확인한 탭 선택",
        "browser_close" => "목록에서 확인한 탭 닫기",
        "browser_close_site" => "사이트 탭 닫기 · " + action.Value,
        "mail_select" => $"{action.Number}번째 메일 열기",
        "timer" => $"타이머 만들기 · {action.Number}초 · {action.Value}",
        "list_timers" => "진행 중인 타이머 확인",
        "cancel_timer" => "선택한 타이머 취소",
        "cancel_timers" => "모든 타이머 취소",
        "volume" or "volume_delta" => "음량 조절",
        "volume_status" => "현재 음량 확인",
        "display_off" => "모니터 화면 끄기 · 마우스나 키 입력으로 다시 켤 수 있어요",
        "current_time" => "현재 시각 확인",
        "read_screen" => "선택한 창 읽기",
        "youtube_find" or "youtube_search" => "유튜브 영상 찾기 · " + action.Value,
        "youtube_play" => "유튜브 재생 · " + action.Value,
        "youtube_select" => $"{action.Number}번째 영상 재생",
        "youtube_list" => "영상 목록 확인",
        "youtube_close" => "영상 창 닫기",
        "seek" => "영상 재생 위치 변경",
        "media" => "미디어 제어 · " + action.Value,
        _ => "답변 준비"
    };
}
