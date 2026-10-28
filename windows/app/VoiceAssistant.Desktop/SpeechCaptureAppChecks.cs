using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class App
{
    int RunSpeechCaptureAppChecks()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture();
        var now = DateTimeOffset.UtcNow; var rows = new List<object>(); int failed = 0;
        void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        void Reset(string activation = "voice")
        {
            StopTurn(); StopInput(); settings = new UiSettings { Speak = false, Activation = activation }; window.LoadSettings(settings);
            microphonePause.Enabled = false; speechCaptureState = new(() => now); microphoneHealth.Begin(manual: true);
            listening = true; starting = busy = speaking = holding = dictating = false; lastUtterance = signalPartials = 0; awakeUntil = DateTimeOffset.MinValue; speechTailUntil = DateTimeOffset.MinValue;
        }
        void Ack(int id, bool enabled) => OnSpeech(JsonSerializer.SerializeToElement(new { type = "capture", request = id, enabled, utterance = 21 }));
        void Level(int id, bool enabled) => OnSpeech(JsonSerializer.SerializeToElement(new { type = "level", rms = .03, peak = .1, value = .36, recognizing = enabled, capture_request = id }));
        void Check(string name, Action work)
        {
            try
            {
                work(); Require(!input.Running && input.ProcessId == 0 && !window.IsVisible && !overlay.IsVisible && PresentationSource.FromVisual(window) == null && PresentationSource.FromVisual(overlay) == null, "Test opened input or a native surface");
                rows.Add(new { name, pass = true });
            }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
        }
        try
        {
            Check("Actual App repairs a missing enable acknowledgement while live level packets continue and keeps the new utterance fence", () =>
            {
                Reset(); CaptureInput(true); var first = input.CaptureRequest; Level(first, false); now += TimeSpan.FromSeconds(4); Level(first, false);
                CheckSpeechCaptureState(); Require(input.CaptureRequest == first + 1 && speechCapturePending && !microphoneHealth.MissingAudio, "Moving meter hid missing recognition or the repair unfenced commands");
                Require(window.StatusTitle.Text == "음성 인식 입력 확인 중", "UI claimed to be listening before acknowledgement");
                Ack(first, true); Require(speechCapturePending, "Stale acknowledgement released repair fence");
                Ack(input.CaptureRequest, false); Require(speechCapturePending && lastUtterance == 0, "Wrong enable state advanced the utterance fence");
                Ack(input.CaptureRequest, true); Require(!speechCapturePending && lastUtterance == 20, "Fresh acknowledgement did not establish new utterance boundary");
                var before = timers.Items.Count;
                OnSpeech(JsonSerializer.SerializeToElement(new { type = "final", utterance = 20, text = "5분 타이머", command = "5분 타이머", has_wake = true }));
                Require(timers.Items.Count == before, "Pre-repair final executed");
                OnSpeech(JsonSerializer.SerializeToElement(new { type = "partial", utterance = 21, text = "새 발화" }));
                Require(signalPartials == 1, "Clean post-repair utterance was dropped");
            });
            Check("Actual App reconnects once after an unsuccessful corrective enable without opening capture in the fixture", () =>
            {
                Reset();
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    // Model a supervisor retry becoming ready without starting a real input process.
                    if (attempt > 0) { microphoneRecoveryPending = starting = false; listening = true; microphoneHealth.Begin(manual: false); }
                    CaptureInput(true); now += TimeSpan.FromSeconds(4); CheckSpeechCaptureState();
                    var generation = input.Generation; now += TimeSpan.FromSeconds(4); CheckSpeechCaptureState();
                    Require(!listening && input.Generation == generation + 1, "Failed gate did not stop the old worker");
                    Require(microphoneRecoveryPending == (attempt < 3) && starting == (attempt < 3), "Recovery exceeded its three-per-minute supervisor limit");
                    CheckSpeechCaptureState(); Require(input.Generation == generation + 1, "Same failure requested recovery repeatedly");
                }
            });
            Check("Actual App detects an acknowledged decoder that becomes paused and ignores old paused frames", () =>
            {
                Reset(); CaptureInput(true); var old = input.CaptureRequest; Ack(old, true); CaptureInput(true); Ack(input.CaptureRequest, true);
                var current = input.CaptureRequest; Level(old, false); now += TimeSpan.FromSeconds(5); CheckSpeechCaptureState(); Require(input.CaptureRequest == current, "Old paused frame reset current input");
                Level(current, false); now += TimeSpan.FromSeconds(2); CheckSpeechCaptureState(); Require(input.CaptureRequest == current + 1 && speechCapturePending, "Current paused decoder was not repaired");
            });
            Check("Actual App never resumes a stopped microphone intentional hold idle AI work call pause or speech tail", () =>
            {
                foreach (var pause in new[] { "stopped", "hold", "busy", "call", "tail" })
                {
                    Reset(pause == "hold" ? "hold" : "voice"); CaptureInput(true);
                    if (pause == "stopped") StopInput();
                    if (pause == "busy") busy = true;
                    if (pause == "call") { microphonePause.Enabled = true; microphonePause.Observe(new(true, new[] { new MicrophoneUser(999999, "fixture call") }, 1, DateTimeOffset.UtcNow)); }
                    if (pause == "tail") speechTailUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                    if (pause != "stopped") CaptureInput(false);
                    if (pause == "hold")
                    {
                        Ack(input.CaptureRequest, true);
                        OnSpeech(JsonSerializer.SerializeToElement(new { type = "partial", utterance = 21, text = "의도하지 않은 입력" }));
                        Require(speechCapturePending && signalPartials == 0, "Unexpected enable acknowledgement released intentional hold pause");
                    }
                    var before = input.CaptureRequest; now += TimeSpan.FromMinutes(1); CheckSpeechCaptureState();
                    Require(input.CaptureRequest == before && !microphoneRecoveryPending, "Intentional pause was overridden: " + pause);
                }
            });
            Check("Actual App repairs a lost resume during an explicit hold or a pending send preview without changing the owning intent", () =>
            {
                Reset("hold"); holding = true; CaptureInput(true); var request = input.CaptureRequest; now += TimeSpan.FromSeconds(4); CheckSpeechCaptureState();
                Require(input.CaptureRequest == request + 1 && holding, "Hold answer intent was lost");
                Reset(); busy = true; sendPreview = new(); sendPreview.Begin(); CaptureInput(true); request = input.CaptureRequest; now += TimeSpan.FromSeconds(4); CheckSpeechCaptureState();
                Require(input.CaptureRequest == request + 1 && busy && AwaitingSendPreview, "Pending send answer could not recover its active input"); CancelSendPreview();
            });
        }
        finally { StopInput(); StopTurn(); overlay.Close(); window.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "speech-capture-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, microphoneCapture = false, speakerPlayback = false, visibleWindows = false, checks = rows }, SettingsStore.Json));
        return failed == 0 ? 0 : 1;
    }
}
