using System;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

public partial class App
{
    readonly MicrophoneHealth microphoneHealth = new();
    bool microphoneRecoveryPending, automaticSpeechStart;
    DateTimeOffset microphoneRetryAt, microphoneStartedAt, nextSignalLog;
    int signalFrames, signalPartials, signalFinals;
    double signalPeak;

    void StopInput()
    {
        CancelExecutionOffer();
        CancelMultilingualStartup();
        if (settings.Language != "ko") WhisperEngine.Shared.Stop();
        CancelWakeCheck();
        microphoneRecoveryPending = automaticSpeechStart = waitingBarge = false; speechCapturePending = true;
        microphoneHealth.Stop(); input.Stop(); listening = starting = false;
    }
    void SpeechFailed(string message)
    {
        if (listening || automaticSpeechStart) RecoverMicrophone(message);
        else { StopInput(); ShowError(message); RefreshStatus(); }
    }
    void RecoverMicrophone(string reason)
    {
        var allowed = microphoneHealth.TryRecover();
        StopInput(); holding = false; window.SetPartial(""); window.SetLevel(0);
        AppPaths.Log("microphone-recovery", (allowed ? "retry: " : "stopped: ") + reason);
        if (!allowed)
        {
            ShowError("마이크 연결이 반복해서 끊겼어요. 입력 장치를 확인한 뒤 ‘듣기 시작’을 눌러 주세요.");
            RefreshStatus(); return;
        }
        microphoneRecoveryPending = starting = true;
        microphoneRetryAt = DateTimeOffset.UtcNow.AddSeconds(2);
        RefreshStatus();
    }
    void CheckMicrophoneHealth()
    {
        if (closing) return;
        if (languageStartup != null) return;
        if (microphoneRecoveryPending)
        {
            if (DateTimeOffset.UtcNow >= microphoneRetryAt && !personalEditorOpen && !busy && !speaking)
            { microphoneRecoveryPending = starting = false; StartListening(automatic: true); }
            return;
        }
        if (starting && DateTimeOffset.UtcNow - microphoneStartedAt > TimeSpan.FromSeconds(30))
        { SpeechFailed("마이크 준비 시간이 초과됐어요."); return; }
        var quiet = microphoneHealth.Quiet;
        microphoneHealth.Evaluate(listening && (!busy && !speaking || CanListenForBarge) && !microphonePause.Blocked,
            microphonePause.Manual || holding);
        if (microphoneHealth.MissingAudio) { RecoverMicrophone("8 seconds without audio frames"); return; }
        if (quiet != microphoneHealth.Quiet)
        {
            AppPaths.Log("microphone-signal", microphoneHealth.Quiet ? "near silence; check mute or input device" : "signal notice cleared");
            RefreshStatus();
        }
    }
    void ObserveMicrophoneSignal(JsonElement message)
    {
        var value = message.GetProperty("value").GetDouble();
        var rms = message.TryGetProperty("rms", out var r) ? r.GetDouble() : value / 12;
        var peak = message.TryGetProperty("peak", out var p) ? p.GetDouble() : rms;
        var recognizing = message.TryGetProperty("recognizing", out var c) && c.GetBoolean();
        var quiet = microphoneHealth.Quiet;
        microphoneHealth.Observe(rms, peak, recognizing);
        if (quiet != microphoneHealth.Quiet) RefreshStatus();
        signalFrames++; signalPeak = Math.Max(signalPeak, rms);
        if (DateTimeOffset.UtcNow < nextSignalLog) return;
        AppPaths.Log("speech-signal", FormattableString.Invariant($"frames={signalFrames}; maxRms={signalPeak:F7}; recognizing={recognizing}; partials={signalPartials}; finals={signalFinals}"));
        signalFrames = signalPartials = signalFinals = 0; signalPeak = 0;
        nextSignalLog = DateTimeOffset.UtcNow.AddSeconds(30);
    }
}
