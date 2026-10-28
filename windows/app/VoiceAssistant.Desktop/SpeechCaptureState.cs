using System;

namespace VoiceAssistant.Desktop;

internal enum CaptureRepair { None, Resend, Reconnect }

// A moving audio meter is not proof that the decoder accepted the latest gate.
internal sealed class SpeechCaptureState(Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    int request = -1, repairs;
    bool? enabled;
    bool acknowledged, reported;
    DateTimeOffset requestedAt;
    DateTimeOffset? mismatchSince;
    internal void Reset()
    { request = -1; repairs = 0; enabled = null; acknowledged = reported = false; mismatchSince = null; }
    internal void Requested(int id, bool value, bool repairing = false)
    {
        request = id; enabled = value; acknowledged = reported = false; requestedAt = now(); mismatchSince = null;
        if (!repairing) repairs = 0;
    }
    internal bool Acknowledge(int id, bool value)
    {
        if (id != request) return false;
        acknowledged = value == enabled;
        if (acknowledged) mismatchSince = null; else mismatchSince ??= now();
        return acknowledged;
    }
    internal void Observe(int id, bool recognizing)
    {
        if (id != request || enabled != true) return;
        if (recognizing && acknowledged) { mismatchSince = null; repairs = 0; }
        else if (!recognizing) mismatchSince ??= now();
    }
    internal CaptureRepair Check(bool desired)
    {
        if (!desired) { mismatchSince = null; return CaptureRepair.None; }
        if (reported) return CaptureRepair.None;
        bool overdue;
        if (enabled != true) { mismatchSince ??= now(); overdue = now() - mismatchSince.Value >= TimeSpan.FromSeconds(2); }
        else if (!acknowledged) overdue = now() - requestedAt >= TimeSpan.FromSeconds(4);
        else overdue = mismatchSince is { } since && now() - since >= TimeSpan.FromSeconds(2);
        if (!overdue) return CaptureRepair.None;
        reported = true;
        return repairs++ == 0 ? CaptureRepair.Resend : CaptureRepair.Reconnect;
    }
}
