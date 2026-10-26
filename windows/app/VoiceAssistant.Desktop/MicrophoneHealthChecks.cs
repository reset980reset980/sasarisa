using System;
using System.Collections.Generic;

namespace VoiceAssistant.Desktop;

internal static class MicrophoneHealthChecks
{
    internal static int Run(List<object> rows)
    {
        var failed = 0;
        void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        void Check(string name, Action test)
        { try { test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        Check("Quiet microphone warns after two minutes, meaningful sound clears it", () =>
        {
            var now = DateTimeOffset.UtcNow; var h = new MicrophoneHealth(() => now);
            h.Begin(true); h.Evaluate(true, false); h.Observe(.00001, .00003, true);
            now = now.AddSeconds(119); h.Observe(.00001, .00003, true); h.Evaluate(true, false);
            Require(!h.Quiet && !h.MissingAudio, "Premature warning or fresh frame treated as missing");
            now = now.AddSeconds(1); h.Observe(.00001, .00003, true); h.Evaluate(true, false);
            Require(h.Quiet, "Two minute silence hidden");
            h.Observe(.02, .1, true); Require(!h.Quiet, "Sound did not clear warning");
        });
        Check("Explicit speaking warns on six seconds of near silence without restarting healthy input", () =>
        {
            var now = DateTimeOffset.UtcNow; var h = new MicrophoneHealth(() => now);
            h.Begin(true); h.Evaluate(true, true); h.Observe(0, 0, true);
            now = now.AddSeconds(5); h.Observe(0, 0, true); h.Evaluate(true, true); Require(!h.Quiet, "Early manual notice");
            now = now.AddSeconds(1); h.Observe(0, 0, true); h.Evaluate(true, true);
            Require(h.Quiet && !h.MissingAudio, "Silence confused with disconnected input");
        });
        Check("Disabled decoding and intentional pause clear quiet state and give fresh reconnect grace", () =>
        {
            var now = DateTimeOffset.UtcNow; var h = new MicrophoneHealth(() => now);
            h.Begin(true); h.Evaluate(true, true); h.Observe(0, 0, true);
            now = now.AddSeconds(7); h.Observe(0, 0, false); h.Evaluate(true, true); Require(!h.Quiet, "Disabled capture warned");
            h.Evaluate(false, false); now = now.AddMinutes(5);
            Require(!h.MissingAudio, "Paused microphone requested recovery");
            h.Evaluate(true, false); Require(!h.MissingAudio, "No resume grace");
            now = now.AddSeconds(7); Require(!h.MissingAudio, "Grace too short");
            now = now.AddSeconds(1); Require(h.MissingAudio, "Missing frames not detected");
            h.Stop(); Require(!h.MissingAudio && !h.Quiet, "Stopped microphone recovered");
        });
        Check("Signal peaks and invalid measurements cannot be classified as digital silence", () =>
        {
            var now = DateTimeOffset.UtcNow; var h = new MicrophoneHealth(() => now);
            h.Begin(true); h.Evaluate(true, true); h.Observe(0, 0, true);
            now = now.AddSeconds(8); h.Observe(.00001, .02, true); h.Evaluate(true, true); Require(!h.Quiet, "Peak ignored");
            h.Observe(0, 0, true); now = now.AddSeconds(8); h.Observe(double.NaN, 0, true); h.Evaluate(true, true);
            Require(!h.Quiet, "Invalid data treated as silence");
        });
        Check("Recovery survives automatic restarts but is bounded and resets for explicit retry", () =>
        {
            var now = DateTimeOffset.UtcNow; var h = new MicrophoneHealth(() => now);
            h.Begin(true);
            for (var i = 0; i < 3; i++) { Require(h.TryRecover(), "Recovery denied too early"); h.Begin(false); }
            Require(!h.TryRecover(), "Restart storm not bounded");
            now = now.AddMinutes(1); Require(h.TryRecover(), "Retry window did not expire");
            h.TryRecover(); h.TryRecover(); Require(!h.TryRecover(), "Budget unexpectedly reset");
            h.Begin(true); Require(h.TryRecover(), "Manual retry did not reset budget");
        });
        return failed;
    }
}
