using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    static readonly HttpClient growHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    GrowTogether? grow;
    CancellationTokenSource? growStop;
    Task growTask = Task.CompletedTask;
    int growGeneration;
    (SharedPair Pair, DateTimeOffset At)? lastShared;

    void RestartGrow()
    {
        var generation = ++growGeneration; growStop?.Cancel(); lastShared = null;
        var previous = growTask; growTask = RunGrowAfterAsync(previous, generation, SettingsTransfer.Copy(settings));
    }
    async Task RunGrowAfterAsync(Task previous, int generation, UiSettings preferences)
    {
        try { await previous; } catch (Exception) { }
        if (closing || generation != growGeneration) return;
        if (preferences.GrowTogether == "off") { window.SetGrowStatus("공유 학습 꺼짐 · 이 PC에서만 배워요."); return; }
        using var own = new CancellationTokenSource(); growStop = own;
        void Status(string text) => Dispatcher.BeginInvoke(() => { if (!closing && generation == growGeneration) window.SetGrowStatus(text); });
        try
        {
            grow ??= new GrowTogether(Path.Combine(AppPaths.Home, "grow-state.json"), growHttp);
            Status("공유 명령을 확인하고 있어요.");
            do
            {
                Status(await Task.Run(() => grow.SyncAsync(preferences, own.Token)));
                await Task.Delay(TimeSpan.FromHours(1), own.Token);
            } while (!own.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppPaths.Log("grow-error", ex.GetType().Name);
            Status("공유 학습이 멈췄어요. 기록·저장 공간을 확인한 뒤 설정을 다시 저장하세요.");
        }
        finally { if (ReferenceEquals(growStop, own)) growStop = null; }
    }
    CommandPlan? ResolveWithShared(string text, CommandPlan? known, out SharedPair? used)
    {
        used = null;
        personal.BeginLearningCommand(text);
        var local = RemoteExecution.Current == null;
        if (local && lastShared is { } recent && DateTimeOffset.UtcNow - recent.At < TimeSpan.FromSeconds(30) &&
            SharedCommands.Key(text) is "아니" or "그게아니야" or "틀렸어")
        {
            grow!.Veto(recent.Pair); lastShared = null;
            return new("방금 사용한 공유 표현은 이 PC에서 다시 실행하지 않도록 제외했어요. 이미 실행한 동작은 되돌리지 않았습니다.", Array.Empty<AssistantAction>());
        }
        if (local) lastShared = null;
        var plan = personal.Resolve(text) ?? known;
        if (plan == null && settings.GrowTogether != "off" && grow?.Lookup(text) is { } shared)
        { used = shared.Pair; return shared.Plan; }
        return plan;
    }
    void RecordSharedLearning(string text, CommandPlan plan)
    {
        if (RemoteExecution.Current != null || settings.GrowTogether != "together") return;
        try { grow?.Record(text, plan, settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { window.SetGrowStatus("공유할 표현을 저장하지 못했어요. 이 PC의 실행과 답변은 유지됩니다."); }
    }
    async Task StopGrowAsync()
    {
        growGeneration++; growStop?.Cancel();
        try { await growTask.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { AppPaths.Log("grow-stop", "Learning sync shutdown wait expired"); }
    }
}
