using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    FreeModeOffer freeModeOffer = new();
    (CancellationToken Token, ActionEffect Effect)? offerAfterTurn;
    const string FreeModeQuestion = "다음부터는 확인한 대상의 전송·게시·삭제를 묻지 않고 바로 할까요? 결제와 대상을 확인하지 못한 동작은 계속 물어봅니다. 20초 안에 ‘네’ 또는 ‘아니’로 답하거나 아래 버튼을 선택하세요.";

    void ConfirmedExecutionSucceeded(ActionEffect effect, CancellationToken token)
    {
        if (token.IsCancellationRequested || turn?.Token != token || !FreeModeOffer.Eligible(settings, effect, RemoteExecution.Current != null)) return;
        offerAfterTurn ??= (token, effect);
    }
    void CancelExecutionOffer()
    {
        offerAfterTurn = null; freeModeOffer.Cancel(); window?.SetFreeModeOffer(false);
    }
    void SubmitFreeModeAnswer(bool yes)
    {
        if (busy || speaking || !freeModeOffer.Pending) { window.SetFreeModeOffer(false); return; }
        _ = SubmitAsync(yes ? "네" : "아니", false);
    }
    void SaveExecutionChoice(bool enabled, bool asked)
    {
        var next = settings with { FreeMode = enabled, FreeModeAsked = asked };
        SettingsStore.Save(next); // Persist with the existing backup before changing live rights.
        settings = next;
        window.UpdateExecutionConsent(next);
    }
    async Task OfferExecutionModeAsync(CancellationToken token)
    {
        var completed = offerAfterTurn; offerAfterTurn = null;
        if (completed is not { } candidate || candidate.Token != token || token.IsCancellationRequested || turn?.Token != token ||
            !FreeModeOffer.Eligible(settings, candidate.Effect, RemoteExecution.Current != null)) return;
        var previous = settings;
        SaveExecutionChoice(false, true);
        window.AddMessage("assistant", FreeModeQuestion);
        await SayAsync(FreeModeQuestion, token);
        token.ThrowIfCancellationRequested();
        if (turn?.Token != token || settings.FreeMode) return;
        // The answer window starts after speech ends. This never starts listening.
        if (freeModeOffer.Begin(previous, candidate.Effect, false)) window.SetFreeModeOffer(true);
    }
    async Task<bool> AnswerExecutionOfferAsync(string text, CancellationToken token)
    {
        var answer = freeModeOffer.Answer(text, RemoteExecution.Current != null);
        window.SetFreeModeOffer(false);
        if (answer == FreeModeAnswer.None) return false;
        token.ThrowIfCancellationRequested();
        SaveExecutionChoice(answer == FreeModeAnswer.Yes, true);
        var reply = answer == FreeModeAnswer.Yes ? "자유 모드를 켰어요. 결제와 대상을 확인하지 못한 동작은 계속 확인할게요." : "계속 실행 전에 확인할게요.";
        window.AddMessage("assistant", reply); await SayAsync(reply, token); return true;
    }
}

public partial class MainWindow
{
    public event Action<bool>? FreeModeAnswered;
    void FreeModeYes_Click(object sender, System.Windows.RoutedEventArgs e) => FreeModeAnswered?.Invoke(true);
    void FreeModeNo_Click(object sender, System.Windows.RoutedEventArgs e) => FreeModeAnswered?.Invoke(false);
    internal void SetFreeModeOffer(bool show) => OnUi(() => FreeModeOfferCard.Visibility = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed);
    internal void UpdateExecutionConsent(UiSettings current) => OnUi(() =>
    {
        var draft = FreeModeInput.IsChecked != _loadedSettings.FreeMode;
        _loadedSettings = _loadedSettings with { FreeMode = current.FreeMode, FreeModeAsked = current.FreeModeAsked };
        if (!draft) FreeModeInput.IsChecked = current.FreeMode;
    });
}
