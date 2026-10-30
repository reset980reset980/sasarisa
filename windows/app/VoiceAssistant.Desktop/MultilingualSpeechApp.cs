using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    CancellationTokenSource? languageRecognition;
    Task languageRecognitionTask = Task.CompletedTask;
    bool languageRecognitionBusy;
    CancellationTokenSource? languageStartup;
    Task languageStartupTask = Task.CompletedTask;
    void CancelMultilingualStartup()
    {
        var previous = languageStartup; languageStartup = null;
        if (previous != null) { previous.Cancel(); if (!listening) starting = false; }
    }
    async Task StartMultilingualInputAsync(UiSettings options, CancellationTokenSource own, bool automatic)
    {
        try
        {
            window.SetStatus("음성 인식을 준비하는 중", "로컬 Whisper 엔진을 준비한 뒤 마이크를 연결해요.", false, true);
            if (!await ModelDownload.MatchesAsync(AppPaths.VadModel, MultilingualSpeech.SpeechDetector, own.Token))
                throw new System.IO.IOException("다국어 말 감지 파일이 없거나 손상됐어요. Windows 설치 프로그램으로 복구해 주세요.");
            if (options.WhisperMode is "cpu" or "cuda") await WhisperEngine.Shared.CheckAsync(options.WhisperMode, own.Token);
            if (own.IsCancellationRequested || languageStartup != own || closing || !ReferenceEquals(options, settings)) return;
            lastUtterance = 0; microphoneHealth.Begin(manual: !automatic); automaticSpeechStart = automatic;
            microphoneStartedAt = DateTimeOffset.UtcNow; input.Start(options); RefreshStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (languageStartup == own && !own.IsCancellationRequested) { StopInput(); ShowError(ex.Message); RefreshStatus(); }
        }
        finally { if (languageStartup == own) languageStartup = null; own.Dispose(); }
    }
    void CancelMultilingualRecognition()
    {
        var previous = languageRecognition; languageRecognition = null;
        if (languageRecognitionBusy) { languageRecognitionBusy = false; busy = turn != null; }
        previous?.Cancel();
    }
    void BeginMultilingualRecognition(JsonElement message)
    {
        if (!message.TryGetProperty("wav", out var audio) || audio.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(audio.GetString())) return;
        if (speaking && !CanListenForBarge) return;
        var options = settings; var session = awakeUntil > DateTimeOffset.Now || dictating || holding;
        var asDictation = dictating; var barge = speaking;
        CancelWakeCheck();
        if (!barge) { busy = true; CaptureInput(false); RefreshStatus(); window.SetStatus("말씀을 인식하는 중", ConversationLanguage.Get(options.Language).Label + " · 로컬 Whisper로 확인하고 있어요.", listening, true); }
        var own = new CancellationTokenSource(); languageRecognition = own; languageRecognitionBusy = !barge;
        languageRecognitionTask = RecognizeMultilingualAsync(options, audio.GetString()!, own, session, asDictation, barge, input.Generation, input.CaptureRequest, speechVersion);
    }
    async Task RecognizeMultilingualAsync(UiSettings options, string wav, CancellationTokenSource own, bool session, bool asDictation, bool barge, int generation, int capture, int outputVersion)
    {
        try
        {
            using var screenAccess = ScreenAccess.Begin(options.BrainAccess,own.Token);
            var target = asDictation ? hands.CaptureInputTarget() : null;
            var transcript = await WhisperEngine.Shared.RefineAsync(WhisperWake.WithPrompt(options), wav, own.Token);
            if (own.IsCancellationRequested || languageRecognition != own || !ReferenceEquals(options, settings) ||
                generation != input.Generation || capture != input.CaptureRequest || !listening || closing || microphonePause.Blocked) return;
            var result = MultilingualSpeech.Decide(options, transcript, session, asDictation);
            if (barge)
            {
                if (!CanListenForBarge || outputVersion != speechVersion) return;
                var confirmed = MultilingualSpeech.Decide(options, transcript, true, false);
                if (!confirmed.Accepted) return;
                if (!bargeIn.Any && MultilingualSpeech.AfterWake(transcript.Text, options) == null) return;
                languageRecognition = null; languageRecognitionBusy = false;
                if (!TryBargeText(JsonSerializer.SerializeToElement(new { text = transcript.Text, has_wake = result.Wake }))) return;
                result = confirmed;
            }
            if (!result.Accepted) return;
            languageRecognition = null; languageRecognitionBusy = false; // Submit/Capture may cancel unrelated pending recognition.
            if (!barge) busy = turn != null;
            if (settings.Activation == "hold") holding = false;
            microphonePause.EndManual();
            if (result.Wake) awakeUntil = DateTimeOffset.Now.AddSeconds(settings.FollowUpSeconds);
            if (result.Text.Length > 0) await SubmitAsync(result.Text, true, interruptedSpeech: barge, dictationInputTarget: target);
            else
            {
                overlay.SetContent("말씀하세요", "호출어를 확인했어요. 이어서 말씀하세요.", false); overlay.ShowOverlay();
                CaptureInput(ShouldCaptureSpeech()); RefreshStatus();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (languageRecognition == own && !own.IsCancellationRequested)
                window.AddMessage("system", "음성을 확인하지 못해 명령을 실행하지 않았어요. 로컬 Whisper 설정을 확인하고 다시 말씀하세요. " + ex.Message);
        }
        finally
        {
            if (languageRecognition == own)
            {
                languageRecognition = null; languageRecognitionBusy = false;
                if (!barge) { busy = turn != null; holding = false; microphonePause.EndManual(); CaptureInput(ShouldCaptureSpeech()); RefreshStatus(); }
            }
            own.Dispose();
        }
    }
}
