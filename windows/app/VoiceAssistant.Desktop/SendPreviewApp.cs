using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class App
{
    SendPreview? sendPreview;
    bool AwaitingSendPreview => sendPreview?.Pending == true;
    void CancelSendPreview() { sendPreview?.Cancel(); window?.SetSendPreview(false, ""); }
    bool AnswerSendPreview(string text, bool remote)
    {
        var pending = sendPreview;
        if (pending == null) return false;
        var answered = pending.Answer(text, remote);
        if (answered) { window.AddMessage("user", text); CaptureInput(false); }
        return answered;
    }
    async Task<bool> ConfirmSendPreviewAsync(string description, CancellationToken token)
    {
        if (RemoteExecution.Current != null) throw new AgentStoppedException("전송 전 미리 보기는 PC에서 확인해야 해요. 휴대폰에서는 전송하지 않았어요.");
        token.ThrowIfCancellationRequested(); var own = new SendPreview(); sendPreview = own;
        try
        {
            window.AddMessage("assistant", description + "\n\n20초 안에 ‘네’ 또는 보내기를 선택해야 전송합니다. ‘아니’ 또는 중지는 전송을 취소합니다.");
            await SayAsync("이 내용으로 보낼까요? 네 또는 아니로 답해 주세요.", token);
            token.ThrowIfCancellationRequested(); own.Begin(); window.SetSendPreview(true, description);
            if (listening && settings.Activation != "hold") awakeUntil = DateTimeOffset.Now.AddSeconds(20);
            CaptureInput(ShouldCaptureSpeech()); RefreshStatus();
            return await own.WaitAsync(token);
        }
        finally
        {
            own.Cancel();
            if (ReferenceEquals(sendPreview, own))
            {
                sendPreview = null; window.SetSendPreview(false, "");
                if (turn?.Token == token) { busy = true; CaptureInput(false); RefreshStatus(); }
            }
        }
    }
    async Task<SelectedClaudeSession?> ReviewClaudeDraftAsync(ClaudeSessions sessions, string text, string description, CancellationToken token)
    {
        var sent = false;
        try
        {
            await sessions.StagePreviewAsync(text, token);
            if (!await ConfirmSendPreviewAsync(description, token)) return null;
            token.ThrowIfCancellationRequested(); var result = await sessions.SendAsync(text, token); sent = true; return result;
        }
        finally
        {
            if (!sent)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    if (!await sessions.CancelPreviewAsync(cleanup.Token)) window.AddMessage("system", "Claude 초안이나 세션이 바뀌어 지우지 않았어요. 입력칸과 전송 상태를 직접 확인하세요.");
                }
                catch (Exception ex) { AppPaths.Log("claude-preview-cleanup", ex.Message); window.AddMessage("system", "Claude 미리 보기 초안을 정리하지 못했어요. 입력칸과 전송 상태를 직접 확인하세요."); }
            }
        }
    }
}

public partial class MainWindow
{
    public event Action<bool>? SendPreviewAnswered;
    void SendPreviewYes_Click(object sender, RoutedEventArgs e) => SendPreviewAnswered?.Invoke(true);
    void SendPreviewNo_Click(object sender, RoutedEventArgs e) => SendPreviewAnswered?.Invoke(false);
    internal void SetSendPreview(bool show, string description) => OnUi(() =>
    { SendPreviewText.Text = description; SendPreviewCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed; });
}
