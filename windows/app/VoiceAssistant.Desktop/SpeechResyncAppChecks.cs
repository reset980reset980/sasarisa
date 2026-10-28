using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class App
{
    // Hidden controls only: never Show/ShowOverlay, start capture, or execute a command.
    int RunSpeechResyncAppChecks()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); microphonePause.Enabled = false;
        settings = settings with { Activation = "voice", Speak = false };
        listening = true; starting = false; speechCapturePending = false;
        var rows = new List<object>(); int failed = 0;
        void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        void Packet(string kind, int id) => OnSpeech(JsonSerializer.SerializeToElement(new { type = kind, utterance = id, text = "discarded test words" }));
        void Hidden() => Require(!window.IsVisible && !overlay.IsVisible && PresentationSource.FromVisual(window) == null && PresentationSource.FromVisual(overlay) == null && !input.Running, "A visible surface or microphone was created");
        void Check(string name, Action action) { try { action(); Hidden(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        try
        {
            Check("Actual App resync cancels stale name refinement and ends an automatic command session without a window", () =>
            {
                using var old = new CancellationTokenSource(); wakeCheck = old; awakeUntil = DateTimeOffset.Now.AddSeconds(30);
                Packet("resync", 10);
                Require(old.IsCancellationRequested && wakeCheck == null && lastUtterance == 10 && awakeUntil == DateTimeOffset.MinValue, "Old wake/session survived discarded audio");
            });
            Check("Actual App rejects old partials but accepts the next clean utterance without showing an overlay", () =>
            {
                signalPartials = 0; Packet("partial", 9); Packet("partial", 10); Require(signalPartials == 0, "Old partial crossed the resync fence");
                Packet("partial", 11); Require(signalPartials == 1 && lastUtterance == 10, "New partial was lost");
            });
            Check("Actual App resync preserves hold, dictation and unrelated active output while clearing stale speech", () =>
            {
                holding = true; awakeUntil = DateTimeOffset.MaxValue; Packet("resync", 12);
                Require(holding && awakeUntil == DateTimeOffset.MaxValue, "Hold intent changed");
                holding = false; dictating = true; var until = awakeUntil = DateTimeOffset.Now.AddSeconds(30); Packet("resync", 13);
                Require(dictating && awakeUntil == until, "Dictation intent changed");
                dictating = false; busy = speaking = true; using var active = new CancellationTokenSource(); turn = active; Packet("resync", 14);
                Require(busy && speaking && !active.IsCancellationRequested && lastUtterance == 14, "Unrelated output was cancelled"); turn = null;
            });
        }
        finally { listening = holding = dictating = busy = speaking = false; CancelWakeCheck(); overlay.Close(); window.Close(); }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "speech-resync-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, visibleWindows = 0, microphoneStarted = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
