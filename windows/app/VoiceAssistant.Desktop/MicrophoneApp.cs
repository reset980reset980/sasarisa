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
    void CaptureInput(bool enabled)
    {
        CancelWakeCheck();
        speechCapturePending = true;
        input.Capture(microphonePause.Permit(enabled) && DateTimeOffset.UtcNow >= speechTailUntil);
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
