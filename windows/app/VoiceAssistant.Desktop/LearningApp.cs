using System;
using System.IO;

namespace VoiceAssistant.Desktop;

public partial class App
{
    void RecordLearnedExecution(LearnedInvocation invocation)
    {
        try
        {
            if (!personal.CompleteInvocation(invocation)) window.AddMessage("system", UiText.Get("동작은 끝났지만 학습 내용이 바뀌어 사용 횟수는 갱신하지 않았어요."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppPaths.Log("learning-usage-save", ex.GetType().Name);
            window.AddMessage("system", UiText.Get("동작은 끝났지만 학습 사용 횟수를 저장하지 못했어요. 같은 동작을 자동으로 다시 실행하지 않습니다."));
        }
    }
    void PruneAutomaticLearning()
    {
        try
        {
            var count = personal.PruneUnusedAutomatic();
            if (count > 0) window.AddMessage("system", UiText.Format("30일 동안 사용하지 않은 자동 학습 {0}개를 정리했어요. 이전 저장에서 복원할 수 있어요.", count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { AppPaths.Log("learning-prune", ex.GetType().Name); window.AddMessage("system", UiText.Get("자동 학습을 정리하지 못해 기존 내용을 유지했어요.")); }
    }
}
