using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed class RemoteExecution(CancellationToken token, string? conversation = null)
{
    static readonly AsyncLocal<RemoteExecution?> scope = new();
    readonly List<string> replies = new();
    readonly List<string> notices = new();
    string voiceNotice = "";
    public static RemoteExecution? Current => scope.Value;
    public CancellationToken Token { get; } = token;
    public string ConversationScope { get; } = "remote:" + (conversation ?? Guid.NewGuid().ToString("N"));
    public void VoiceNotice(string text) => voiceNotice = RemoteText.Clip(text, 140);
    public void ExecutionNotice(string text)
    { lock (notices) if (notices.Count < 3 && !notices.Contains(text)) notices.Add(RemoteText.Clip(text, 160)); }
    public void Capture(string role, string text)
    {
        if (role is not ("assistant" or "error") || string.IsNullOrWhiteSpace(text)) return;
        lock (replies) if (replies.Count < 24 && replies.LastOrDefault() != text) replies.Add(RemoteText.Clip(text, 400));
    }
    public async Task<string> RunAsync(Func<Task> action)
    {
        var previous = scope.Value; scope.Value = this;
        try { Token.ThrowIfCancellationRequested(); await action(); }
        finally { scope.Value = previous; }
        string notice; lock (notices) notice = string.Join("\n", notices);
        if (Token.IsCancellationRequested) return RemoteText.Clip(notice + (notice.Length > 0 ? "\n" : "") + "요청을 중지했거나 처리 시간이 초과됐어요. 이미 실행된 동작은 PC 화면에서 확인하세요.", 400);
        lock (replies)
        {
            var answer = replies.Count == 0 ? "완료 응답을 확인하지 못했어요. PC의 비서 화면을 확인하세요." : string.Join("\n", replies);
            return RemoteText.Clip((notice.Length > 0 ? notice + "\n" : "") + (voiceNotice.Length > 0 ? voiceNotice + "\n" : "") + answer, 400);
        }
    }
    public static void Guard(AssistantAction action, bool fromAi)
    {
        if (Current == null) return;
        Current.Token.ThrowIfCancellationRequested();
        if (ActionPolicy.NeedsConfirmation(action, fromAi) || action.Kind is "rewrite" or "dictation")
            throw new AgentStoppedException("이 동작은 PC에서 내용을 확인해야 해요. 휴대폰에서는 실행하지 않았습니다. PC에서 다시 요청해 주세요.");
    }
}

public partial class App
{
    CancellationTokenSource? remoteStop;
    Task remoteTask = Task.CompletedTask;
    int remoteGeneration;

    void RestartRemote()
    {
        var generation = ++remoteGeneration;
        remoteStop?.Cancel();
        var previous = remoteTask;
        remoteTask = RunRemoteAfterAsync(previous, generation);
    }
    async Task RunRemoteAfterAsync(Task previous, int generation)
    {
        try { await previous; } catch (Exception) { /* The previous observer already reported its failure. */ }
        if (closing || generation != remoteGeneration) return;
        if (!settings.ProRemote) { window.SetRemoteStatus("휴대폰 원격 꺼짐"); return; }
        using var own = new CancellationTokenSource(); remoteStop = own;
        void Status(string text) => Dispatcher.BeginInvoke(() => { if (!closing && generation == remoteGeneration) window.SetRemoteStatus(text); });
        try
        {
            var ledger = new RemoteLedger(Path.Combine(AppPaths.Home, "remote-receipts.json"));
            // Capture the saved name preferences; settings changes cancel this generation.
            var requestSettings = settings;
            var pump = new RemotePump(ProAccountClient.Shared, ledger,
                (command, token) => Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, token)).Task.Unwrap(),
                Status, text => RemoteText.Stop(LearnedNames.Strip(text, requestSettings)));
            Status("휴대폰 원격 연결을 확인하고 있어요.");
            await Task.Run(() => pump.RunAsync(own.Token));
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppPaths.Log("remote-error", ex is IOException or UnauthorizedAccessException ? "Remote storage unavailable" : ex.GetType().Name);
            Status("휴대폰 원격이 중지됐어요. 처리 기록·연결 상태를 확인한 뒤 설정을 다시 저장하세요.");
        }
        finally { if (ReferenceEquals(remoteStop, own)) remoteStop = null; }
    }
    async Task<string> RunRemoteCommandAsync(RemoteCommand command, CancellationToken token, bool localPhone = false)
    {
        token.ThrowIfCancellationRequested();
        if (closing || (localPhone ? phoneServer == null && internetPhone?.Linked != true : !settings.ProRemote)) return "이 PC의 휴대폰 원격 연결이 꺼져 있어요.";
        if (busy || speaking || starting || dictating || holding || (listening && awakeUntil > DateTimeOffset.Now) || personalEditorOpen || setupWindow != null || !window.IsEnabled)
            return "PC에서 다른 작업을 처리 중이에요. 끝난 뒤 다시 요청해 주세요.";
        var text = LearnedNames.Strip(command.Text, settings);
        if (string.IsNullOrWhiteSpace(text)) return "호출 이름 뒤에 실행할 내용을 함께 보내 주세요.";
        var context = new RemoteExecution(token, (localPhone ? "phone:" : "pro:") + command.Account + ":" + (command.ConversationId ?? command.Id));
        if (!localPhone) window.SetRemoteStatus("휴대폰 요청을 처리 중이에요.");
        return await context.RunAsync(() => SubmitAsync(text, false));
    }
    async Task StopRemoteAsync()
    {
        remoteGeneration++; remoteStop?.Cancel();
        try { await remoteTask.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { AppPaths.Log("remote-stop", "Remote shutdown wait expired"); }
    }
}
