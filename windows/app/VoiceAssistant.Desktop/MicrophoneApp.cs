using System;
using System.Linq;

namespace VoiceAssistant.Desktop;

public partial class App
{
    readonly MicrophonePause microphonePause = new();
    MicrophoneMonitor? microphoneMonitor;
    bool lastMicrophoneBlocked;
    string lastMicrophoneUsers = "";
    void StartMicrophoneMonitor()
    {
        microphonePause.Enabled = settings.PauseInCalls;
        microphoneMonitor = new MicrophoneMonitor();
        microphoneMonitor.Changed += snapshot => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            var known = microphonePause.Known;
            microphonePause.Observe(snapshot, Environment.ProcessId, input.ProcessId);
            var users = $"available={snapshot.Available}; worker={input.ProcessId}; users=" + string.Join(",", microphonePause.Users.Select(x => x.ProcessId + ":" + x.Name));
            if (users != lastMicrophoneUsers) { lastMicrophoneUsers = users; AppPaths.Log("microphone-users", users); }
            ReconcileMicrophonePause();
            if (known != microphonePause.Known && listening && microphonePause.Blocked) RefreshStatus();
        });
        microphoneMonitor.Start();
    }
    SpeechCaptureState speechCaptureState = new();
    void CaptureInput(bool enabled, bool repairing = false)
    {
        CancelWakeCheck();
        speechCapturePending = true;
        var permitted = microphonePause.Permit(enabled) && DateTimeOffset.UtcNow >= speechTailUntil;
        input.Capture(permitted);
        speechCaptureState.Requested(input.CaptureRequest, permitted, repairing);
    }
    void CheckSpeechCaptureState()
    {
        if (closing || starting || !listening) return;
        var repair = speechCaptureState.Check(ShouldCaptureSpeech() && !microphonePause.Blocked && DateTimeOffset.UtcNow >= speechTailUntil);
        if (repair == CaptureRepair.Resend)
        {
            AppPaths.Log("speech-capture-repair", "resend enable; request=" + input.CaptureRequest);
            CaptureInput(true, repairing: true);
            window.AddMessage("system", "음성 인식 입력 상태를 다시 맞추고 있어요. 잠시 후 이름부터 다시 말씀해 주세요."); RefreshStatus();
        }
        else if (repair == CaptureRepair.Reconnect) RecoverMicrophone("decoder capture acknowledgement/state did not recover", recognitionState: true);
    }
    void ReconcileMicrophonePause()
    {
        var blocked = microphonePause.Blocked;
        if (lastMicrophoneBlocked == blocked) return;
        lastMicrophoneBlocked = blocked;
        if (blocked)
        {
            awakeUntil = DateTimeOffset.MinValue; holding = false;
            CaptureInput(false); window.SetPartial(""); window.SetLevel(0);
            if (listening) AppPaths.Log("microphone-pause", microphonePause.Known ? "external capture active; automatic recognition paused" : "capture status unavailable; automatic recognition paused");
        }
        else
        {
            CaptureInput(ShouldCaptureSpeech());
            if (listening) AppPaths.Log("microphone-pause", microphonePause.Manual ? "manual command allowed" : "external capture ended; automatic recognition resumed");
        }
        RefreshStatus();
    }
    void BeginManualMicrophone()
    {
        microphonePause.BeginManual(TimeSpan.FromSeconds(settings.Activation == "hold" ? 35 : settings.FollowUpSeconds));
        ReconcileMicrophonePause();
    }
}
