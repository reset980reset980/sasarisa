using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    readonly BargeInSession bargeIn = new();
    EchoCalibration? echoCalibration;
    string echoCalibrationKey = "";
    double initialEchoCoupling;
    bool speechCapturePending = true, waitingBarge;
    DateTimeOffset speechTailUntil, bargeRequestedAt, bargeInputUntil;
    bool CanListenForBarge => listening && speaking && bargeIn.Active && settings.Activation != "hold" && !microphonePause.Blocked;
    bool ShouldCaptureSpeech() => listening && (CanListenForBarge || !speaking && (!busy || AwaitingSendPreview) &&
        (settings.Activation == "hold" ? holding : settings.Activation == "voice" || dictating || awakeUntil > DateTimeOffset.Now));
    void VoiceOnUi(int version, Action action)
    {
        if (closing || Dispatcher.HasShutdownStarted) return;
        try { Dispatcher.Invoke(() => { if (!closing && version == speechVersion) action(); }); }
        catch (TaskCanceledException) { }
    }
    VoicePlaybackObserver ObservePlayback(int version, string text, UiSettings options) => new(
        device => VoiceOnUi(version, () =>
        {
            EndSpeechBarge(true, false);
            echoCalibration ??= new(Path.Combine(AppPaths.Home, "echo-calibration.json"));
            echoCalibrationKey = EchoCalibration.Key(settings.Device, device);
            initialEchoCoupling = echoCalibration.Get(echoCalibrationKey);
            bargeIn.Echo.UseCoupling(initialEchoCoupling);
            bargeIn.Begin(text, settings.Name, options.BargeIn, device, WakePhrases.Active(settings));
            speechTailUntil = DateTimeOffset.MinValue;
            CaptureInput(ShouldCaptureSpeech()); RefreshStatus();
            AppPaths.Log("barge-mode", $"mode={options.BargeIn}; anySpeech={bargeIn.Any}; enabled={CanListenForBarge}");
        }),
        rms => { if (version == Volatile.Read(ref speechVersion)) bargeIn.Echo.Played(rms); },
        interrupted => VoiceOnUi(version, () => { EndSpeechBarge(interrupted, !interrupted); CaptureInput(false); }));
    void EndSpeechBarge(bool interrupted, bool tail)
    {
        var wasActive = bargeIn.Active;
        var headphones = bargeIn.Any;
        bargeIn.End(interrupted);
        if (wasActive && !interrupted && bargeIn.Echo.Coupling != initialEchoCoupling)
            echoCalibration?.Save(echoCalibrationKey, bargeIn.Echo.Coupling);
        if (tail && wasActive && !headphones) speechTailUntil = DateTimeOffset.UtcNow.AddSeconds(.6);
        else if (!tail) speechTailUntil = DateTimeOffset.MinValue;
    }
    void CheckSpeechTail()
    {
        if (speechTailUntil != DateTimeOffset.MinValue && DateTimeOffset.UtcNow >= speechTailUntil)
        { speechTailUntil = DateTimeOffset.MinValue; CaptureInput(ShouldCaptureSpeech()); }
        if (waitingBarge && DateTimeOffset.UtcNow - bargeRequestedAt > TimeSpan.FromSeconds(3))
        {
            waitingBarge = false; CaptureInput(ShouldCaptureSpeech());
            window.AddMessage("system", "다시 들을 준비를 했어요. 이어서 말씀하세요.");
        }
    }
    void ObserveCapture(JsonElement message)
    {
        var enabled = message.GetProperty("enabled").GetBoolean();
        AppPaths.Log("speech-capture", "enabled=" + enabled);
        if (message.TryGetProperty("request", out var request) && request.GetInt32() == input.CaptureRequest)
        {
            speechCapturePending = !enabled;
            if (message.TryGetProperty("utterance", out var utterance)) lastUtterance = Math.Max(lastUtterance, utterance.GetInt32() - 1);
        }
    }
    bool TryBargeText(JsonElement message)
    {
        if (!CanListenForBarge || speechCapturePending) return false;
        var text = message.GetProperty("text").GetString() ?? "";
        var wake = message.TryGetProperty("has_wake", out var value) && value.GetBoolean();
        if (!bargeIn.AcceptText(text, wake)) return false;
        InterruptSpeech(false, text); return true;
    }
    void ObserveBargeLevel(JsonElement message)
    {
        var rms = message.TryGetProperty("rms", out var level) ? level.GetDouble() : 0;
        var pitch = message.TryGetProperty("pitch", out var p) ? p.GetDouble() : 0;
        var detected = bargeIn.Listen(rms, pitch);
        if (detected && CanListenForBarge && !speechCapturePending) InterruptSpeech(true, "");
    }
    void InterruptSpeech(bool acoustic, string heard)
    {
        EndSpeechBarge(true, false);
        var greeting = greetingWindow; greetingWindow = null; greeting?.Close();
        var previous = turn; turn = null; previous?.Cancel();
        speechVersion++; output.Stop(); busy = speaking = false;
        bargeInputUntil = DateTimeOffset.UtcNow.AddSeconds(20);
        awakeUntil = DateTimeOffset.Now.AddSeconds(settings.FollowUpSeconds);
        if (acoustic) { waitingBarge = true; bargeRequestedAt = DateTimeOffset.UtcNow; input.BeginBarge(); }
        // Text interruption keeps this decoder stream: resetting it loses the user's first words.
        overlay.SetContent("듣고 있어요", heard.Length == 0 ? "이어서 말씀하세요." : heard, false);
        overlay.ShowOverlay(); window.SetPartial(heard); RefreshStatus();
        AppPaths.Log("barge-in", acoustic ? "voice over measured playback" : "recognized speech; current utterance retained");
    }
}
