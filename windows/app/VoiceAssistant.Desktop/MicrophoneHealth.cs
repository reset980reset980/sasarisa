using System;
using System.Collections.Generic;

namespace VoiceAssistant.Desktop;

// Uses signal measurements only; never stores audio or recognized text.
public sealed class MicrophoneHealth(Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Queue<DateTimeOffset> retries = new();
    DateTimeOffset lastAudio, armedAt;
    DateTimeOffset? quietSince;
    bool active;
    public bool Quiet { get; private set; }
    public bool MissingAudio => active && now() - lastAudio >= TimeSpan.FromSeconds(8) && now() - armedAt >= TimeSpan.FromSeconds(8);
    public void Begin(bool manual)
    {
        Stop(); lastAudio = now();
        if (manual) retries.Clear();
    }
    public void Stop() { active = false; quietSince = null; Quiet = false; }
    public void Observe(double rms, double peak, bool recognizing)
    {
        lastAudio = now();
        if (!active || !recognizing || !double.IsFinite(rms) || !double.IsFinite(peak) || rms < 0 || peak < 0)
        { quietSince = null; Quiet = false; return; }
        if (rms < .0001 && peak < .001) quietSince ??= now();
        else { quietSince = null; Quiet = false; }
    }
    public void Evaluate(bool enabled, bool manual)
    {
        if (enabled != active)
        { active = enabled; armedAt = now(); quietSince = null; Quiet = false; }
        Quiet = active && quietSince is { } since && now() - since >= TimeSpan.FromSeconds(manual ? 6 : 120);
    }
    public bool TryRecover()
    {
        while (retries.Count > 0 && now() - retries.Peek() >= TimeSpan.FromMinutes(1)) retries.Dequeue();
        if (retries.Count >= 3) return false;
        retries.Enqueue(now()); return true;
    }
}
