using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class App
{
    string StopReply => settings.Language == "ko" ? "멈췄어요." : "Stopped.";
    async Task<string> ApplyDictationAsync(string text, CancellationToken token, WindowsActions.InputTarget? expected = null)
    {
        if (DictationProcessor.IsStop(text, settings.Name)) { StopTurn(); return StopReply; }
        var target = expected ?? hands.CaptureInputTarget();
        var edit = await DictationProcessor.PrepareAsync(text, settings, token);
        token.ThrowIfCancellationRequested(); hands.EnsureInputTarget(target);
        if (edit.Utterance.Control == DictationControl.Undo) return await actionHistory.UndoDocumentAsync(token);
        if (edit.Result.Notice != null) window.AddMessage("system", edit.Result.Notice);
        if (edit.Utterance.Control == DictationControl.Text && string.IsNullOrWhiteSpace(edit.Result.Text)) return "말버릇만 들어 있어 입력하지 않았어요.";
        window.AddMessage("system", edit.Description);
        var applied = await DictationWriter.ApplyAsync(edit,
            request => MessageBox.Show(window, $"대상: {hands.TargetLabel}\n\n{request.Description}\n\nEnter 키는 앱에 따라 메시지를 전송할 수 있습니다. 실행할까요?",
                UiText.Get("받아쓰기 입력 확인"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
            (value, ct) => hands.TypeAsync(value, ct, target), async (key, ct) =>
            {
                // Enter may send a message. It must block undo of an earlier draft.
                actionHistory.Record("받아쓰기 키 입력 요청", unavailable: "줄바꿈이나 전송 결과를 확인하세요. 전송한 내용은 자동으로 되돌릴 수 없어요.");
                await hands.KeyAsync(key, ct, target, [hotkeys?.Wake, hotkeys?.Stop]);
            }, token);
        if (!applied) return "받아쓰기 입력을 취소했어요.";
        return edit.Utterance.Control switch
        {
            DictationControl.Undo => "문서에 실행 취소를 요청했어요.",
            DictationControl.Enter => "Enter 키를 눌렀어요.",
            _ when edit.Utterance.EnterAfter => "받아 적고 Enter 키를 눌렀어요.",
            _ => edit.Result.Polished ? "다듬어서 받아 적었어요." : "받아 적었어요."
        };
    }
}
