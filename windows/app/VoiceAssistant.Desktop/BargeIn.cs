using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

// Raw RMS values, not the amplified meter value. Playback runs on an audio thread.
internal sealed class DoubleTalk(Func<DateTimeOffset>? clock = null, double coupling = .12)
{
    readonly object sync = new();
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Queue<(DateTimeOffset Time, double Value)> playback = new();
    readonly Queue<double> quiet = new();
    readonly Queue<bool> recent = new();
    readonly List<double> ratios = new();
    bool active;
    double ambient;
    public double Coupling { get; private set; } = double.IsFinite(coupling) ? Math.Clamp(coupling, .03, .6) : .12;
    public void UseCoupling(double value) { lock (sync) if (!active && double.IsFinite(value)) Coupling = Math.Clamp(value, .03, .6); }
    public void Begin()
    {
        lock (sync)
        {
            playback.Clear(); ratios.Clear(); recent.Clear(); active = true;
            ambient = quiet.Count == 0 ? 0 : quiet.Order().ElementAt(quiet.Count / 2);
        }
    }
    public void Played(double rms)
    {
        if (!double.IsFinite(rms) || rms < 0) return;
        lock (sync)
        {
            if (!active) return;
            var t = now(); playback.Enqueue((t, rms));
            while (playback.Count > 0 && (t - playback.Peek().Time).TotalSeconds > .5) playback.Dequeue();
        }
    }
    public bool Listen(double rms, double pitch)
    {
        if (!double.IsFinite(rms) || rms < 0 || !double.IsFinite(pitch)) return false;
        lock (sync)
        {
            if (!active) { quiet.Enqueue(rms); while (quiet.Count > 10) quiet.Dequeue(); return false; }
            var t = now();
            var output = playback.Where(x => (t - x.Time).TotalSeconds < .3).Select(x => x.Value).DefaultIfEmpty(0).Max();
            // A muted mic provides no evidence about the room's echo coupling.
            if (output > .03 && rms > .0001 && ratios.Count < 1200) ratios.Add(rms / output);
            var loud = rms > Math.Max(.045, ambient * 3) && pitch > .42 && (output <= .01 || rms > output * Coupling * 3);
            recent.Enqueue(loud); while (recent.Count > 4) recent.Dequeue();
            return recent.Count(x => x) >= 3;
        }
    }
    public void End(bool interrupted)
    {
        lock (sync)
        {
            if (!active) return;
            active = false;
            if (!interrupted && ratios.Count >= 20 && ambient < .01)
                Coupling = Math.Clamp(Coupling * .7 + ratios.Order().ElementAt(ratios.Count / 2) * .3, .03, .6);
            playback.Clear(); recent.Clear();
        }
    }
}

internal sealed class BargeInSession
{
    public DoubleTalk Echo { get; }
    public bool Active { get; private set; }
    public bool Any { get; private set; }
    public string Mode { get; private set; } = "off";
    string spoken = "", wake = "";
    public BargeInSession(Func<DateTimeOffset>? clock = null, double coupling = .12) => Echo = new(clock, coupling);
    public static bool Headphones(string name)
    {
        var n = name.ToLowerInvariant();
        return !n.Contains("외장") && new[] { "airpods", "buds", "headphone", "headset", "beats", "이어폰", "헤드폰", "헤드셋", "wh-", "wf-", "qc" }.Any(n.Contains);
    }
    static string Squash(string text) => Regex.Replace(text, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
    public static string DropGreeting(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return text;
        var pattern = @"^" + Regex.Escape(name.Trim()) + @"[\s,.!?·]+(.+)$";
        for (var i = 0; i < 5; i++) { var match = Regex.Match(text.TrimStart(), pattern); if (!match.Success) break; text = match.Groups[1].Value.TrimStart(); }
        return text;
    }
    public void Begin(string text, string name, string mode, string output)
    {
        spoken = Squash(text); wake = Squash(name); Mode = mode;
        Any = mode == "any" || mode == "auto" && Headphones(output);
        Active = mode != "off"; if (Active) Echo.Begin();
    }
    public bool IsEcho(string text)
    {
        var value = Squash(text);
        if (value.Length < 2 || spoken.Contains(value)) return true;
        var letters = spoken.ToHashSet();
        return value.Length >= 4 && value.Count(letters.Contains) / (double)value.Length > .85;
    }
    public bool AcceptText(string text, bool hasWake)
    {
        if (!Active || IsEcho(text)) return false;
        var value = Squash(text);
        return Any || hasWake || wake.Length > 0 && value.Contains(wake) ||
            new[] { "멈춰", "그만", "스톱", "stop", "잠깐", "됐어" }.Any(value.StartsWith);
    }
    public bool Listen(double rms, double pitch)
    {
        var voice = Echo.Listen(rms, pitch);
        return Active && Mode == "auto" && !Any && voice;
    }
    public void End(bool interrupted) { Echo.End(interrupted); Active = false; }
}
