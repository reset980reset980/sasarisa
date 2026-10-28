using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    CancellationTokenSource? wakeCheck;
    Task wakeCheckTask = Task.CompletedTask;
    int wakeCheckUtterance;
    bool CanCheckWake => !closing && listening && !starting && !busy && !speaking && !dictating && !holding &&
        !personalEditorOpen && nameTrainingWindow == null && settings.Activation == "voice" &&
        awakeUntil <= DateTimeOffset.Now && !microphonePause.Blocked && !speechCapturePending && !waitingBarge && DateTimeOffset.UtcNow >= speechTailUntil;
    void CancelWakeCheck()
    {
        var previous = wakeCheck; wakeCheck = null; previous?.Cancel();
    }
    void CheckMissedWake(JsonElement message)
    {
        if (!CanCheckWake || !message.TryGetProperty("wav", out var audio) || audio.ValueKind != JsonValueKind.String ||
            !message.TryGetProperty("duration", out var duration) || !duration.TryGetDouble(out var seconds)) return;
        var live = message.GetProperty("text").GetString() ?? ""; var wav = audio.GetString();
        if (string.IsNullOrWhiteSpace(wav) || !WhisperWake.Candidate(settings, live, seconds)) return;
        var own = new CancellationTokenSource(); wakeCheck = own; wakeCheckUtterance = lastUtterance;
        wakeCheckTask = RecheckWakeAsync(settings, live, wav, own, input.Generation, input.CaptureRequest, lastUtterance);
    }
    async Task RecheckWakeAsync(UiSettings options, string live, string wav, CancellationTokenSource own, int generation, int capture, int utterance)
    {
        try
        {
            var transcript = await WhisperEngine.Shared.TryRefineAsync(WhisperWake.WithPrompt(options), wav, own.Token);
            if (transcript == null || own.IsCancellationRequested || wakeCheck != own || !CanCheckWake ||
                generation != input.Generation || capture != input.CaptureRequest || utterance != lastUtterance || !ReferenceEquals(options, settings)) return;
            var result = WhisperWake.Decide(options, live, transcript);
            if (!result.Wake) return;
            wakeCheck = null;
            awakeUntil = DateTimeOffset.Now.AddSeconds(settings.FollowUpSeconds);
            AppPaths.Log("whisper-wake", result.Command.Length == 0 ? "name only" : "name and verified command");
            overlay.SetContent("말씀하세요", "이름을 다시 확인했어요. 이어서 말씀하세요.", false); overlay.ShowOverlay(); RefreshStatus();
            if (result.Command.Length > 0) await SubmitAsync(result.Command, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (wakeCheck == own && !own.IsCancellationRequested) AppPaths.Log("whisper-wake-error", ex.GetType().Name); }
        finally { if (wakeCheck == own) wakeCheck = null; own.Dispose(); }
    }
}
