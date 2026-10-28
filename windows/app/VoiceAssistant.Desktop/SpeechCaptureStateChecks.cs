using System;
using System.Collections.Generic;

namespace VoiceAssistant.Desktop;

internal static class SpeechCaptureStateChecks
{
    internal static int Run(List<object> rows)
    {
        int failed = 0;
        void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        void Check(string name, Action work) { try { work(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        Check("Live level frames do not hide an unacknowledged decoder enable; repair is bounded", () =>
        {
            var now = DateTimeOffset.UtcNow; var health = new MicrophoneHealth(() => now); var state = new SpeechCaptureState(() => now);
            health.Begin(true); health.Evaluate(true, false); state.Requested(1, true);
            for (var i = 0; i < 4; i++) { now += TimeSpan.FromSeconds(1); health.Observe(.03, .1, false); state.Observe(1, false); }
            Require(!health.MissingAudio && !health.Quiet, "Reproduction lost its healthy moving meter");
            Require(state.Check(true) == CaptureRepair.Resend && state.Check(true) == CaptureRepair.None, "Lost enable did not get exactly one corrective action");
            state.Requested(2, true, repairing: true); now += TimeSpan.FromSeconds(4);
            Require(state.Check(true) == CaptureRepair.Reconnect && state.Check(true) == CaptureRepair.None, "Persistent lost enable did not escalate once");
        });
        Check("Current acknowledgement and recognizing frames keep healthy speech stable", () =>
        {
            var now = DateTimeOffset.UtcNow; var state = new SpeechCaptureState(() => now); state.Requested(2, true); state.Acknowledge(2, true);
            now += TimeSpan.FromMinutes(5); state.Observe(2, true); Require(state.Check(true) == CaptureRepair.None, "Healthy decoder was reset");
        });
        Check("Old capture acknowledgements and old paused frames cannot alter the current request", () =>
        {
            var now = DateTimeOffset.UtcNow; var state = new SpeechCaptureState(() => now); state.Requested(9, true); Require(!state.Acknowledge(8, true) && !state.Acknowledge(9, false), "Wrong request or state was accepted");
            now += TimeSpan.FromSeconds(4); Require(state.Check(true) == CaptureRepair.Resend, "Old ack enabled the current gate");
            state.Requested(10, true, repairing: true); state.Acknowledge(10, true); state.Observe(9, false); now += TimeSpan.FromSeconds(8);
            Require(state.Check(true) == CaptureRepair.None, "Old paused frame reset healthy input");
        });
        Check("Acknowledged decoder that falls back to paused while meters continue is repaired", () =>
        {
            var now = DateTimeOffset.UtcNow; var state = new SpeechCaptureState(() => now); state.Requested(1, true); state.Acknowledge(1, true); state.Observe(1, false);
            now += TimeSpan.FromSeconds(1); Require(state.Check(true) == CaptureRepair.None, "Brief transition reset speech");
            now += TimeSpan.FromSeconds(1); Require(state.Check(true) == CaptureRepair.Resend, "Persistently paused decoder was ignored");
            state.Requested(2, true, repairing: true); state.Acknowledge(2, true); state.Observe(2, false); now += TimeSpan.FromSeconds(2);
            Require(state.Check(true) == CaptureRepair.Reconnect, "Contradictory acknowledgement caused an infinite resend loop");
        });
        Check("Intentional hold idle thinking call pause and stopped input never request enable", () =>
        {
            var now = DateTimeOffset.UtcNow; var state = new SpeechCaptureState(() => now); state.Requested(1, false); state.Acknowledge(1, false);
            now += TimeSpan.FromHours(1); Require(state.Check(false) == CaptureRepair.None, "Intentional pause was enabled");
            Require(state.Check(true) == CaptureRepair.None, "New resume had no transition grace"); now += TimeSpan.FromSeconds(2); Require(state.Check(true) == CaptureRepair.Resend, "Missing resume stayed paused");
            state.Reset(); Require(state.Check(false) == CaptureRepair.None, "Stopped input was restarted");
        });
        Check("Real recognizing feedback ends a repair incident and ordinary requests get a fresh fence", () =>
        {
            var now = DateTimeOffset.UtcNow; var state = new SpeechCaptureState(() => now); state.Requested(1, true); now += TimeSpan.FromSeconds(4); Require(state.Check(true) == CaptureRepair.Resend, "Fixture did not request repair");
            state.Requested(2, true, repairing: true); state.Acknowledge(2, true); state.Observe(2, true); state.Observe(2, false); now += TimeSpan.FromSeconds(2);
            Require(state.Check(true) == CaptureRepair.Resend, "Recovered input inherited the previous incident");
            state.Requested(3, false); state.Requested(4, true); now += TimeSpan.FromSeconds(3); Require(state.Check(true) == CaptureRepair.None, "New request inherited a stale timeout");
        });
        return failed;
    }
}
