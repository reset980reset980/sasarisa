using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class App
{
    async Task<string> ApplyDictationAsync(string text, CancellationToken token, WindowsActions.InputTarget? expected = null)
    {
        if (DictationProcessor.IsStop(text, settings.Name)) { StopTurn(); return "멈췄어요."; }
        var target = expected ?? hands.CaptureInputTarget();
        var edit = await DictationProcessor.PrepareAsync(text, settings, token);
        token.ThrowIfCancellationRequested(); hands.EnsureInputTarget(target);
        if (edit.Result.Notice != null) window.AddMessage("system", edit.Result.Notice);
        if (edit.Utterance.Control == DictationControl.Text && string.IsNullOrWhiteSpace(edit.Result.Text)) return "말버릇만 들어 있어 입력하지 않았어요.";
        window.AddMessage("system", edit.Description);
        var applied = await DictationWriter.ApplyAsync(edit,
            request => MessageBox.Show(window, $"대상: {hands.TargetLabel}\n\n{request.Description}\n\nEnter 키는 앱에 따라 메시지를 전송할 수 있습니다. 실행할까요?",
                "받아쓰기 입력 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
            (value, ct) => hands.TypeAsync(value, ct, target), (key, ct) => hands.KeyAsync(key, ct, target, [hotkeys?.Wake, hotkeys?.Stop]), token,
            () => actionHistory.Record(edit.Utterance.Control == DictationControl.Undo ? "받아쓰기 실행 취소 요청" : "받아쓰기 입력 요청",
                unavailable: "대상 문서에서 실제 반영된 내용을 확인하세요. 실행 취소 단위는 문서 앱이 결정합니다."));
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
