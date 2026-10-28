using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

internal sealed record WhisperWakeResult(bool Wake, string Command);
internal static class WhisperWake
{
    internal static int? Onset(string text)
    {
        var key = LearnedNames.Squash(text);
        if (key.Length == 0 || key[0] is < '가' or > '힣') return null;
        var lead = (key[0] - '가') / 588;
        return lead switch { 0 or 1 or 15 => 0, 3 or 4 or 16 => 3, 7 or 8 or 17 => 7, 9 or 10 or 12 or 13 or 14 => 9, _ => lead };
    }
    public static bool Candidate(UiSettings options, string text, double seconds)
    {
        var names = WakePhrases.Active(options);
        if (options.Activation != "voice" || !WhisperEngine.Enabled(options) || names.Length == 0 ||
            !double.IsFinite(seconds) || seconds <= .5 || seconds >= 4.5 || text.Length > 200 || LearnedNames.Squash(text).Length > 24) return false;
        if (string.IsNullOrWhiteSpace(text)) return names.Any(name => Regex.IsMatch(name, "[a-z]", RegexOptions.IgnoreCase));
        var heard = Onset(text);
        return names.Any(name => Onset(NameLearning.HangulReading(name) ?? name) is not { } onset || heard == null || onset == heard);
    }
    static string[] Names(UiSettings options) => WakePhrases.Active(options).OrderByDescending(x => x.Count(c => !char.IsWhiteSpace(c))).ToArray();
    internal static UiSettings WithPrompt(UiSettings options)
    {
        var names = WakePhrases.Active(options);
        return options with { Text = options.Text with { CustomWords = names.Concat(options.Text.CustomWords).Distinct(StringComparer.OrdinalIgnoreCase).Take(150).ToArray() } };
    }
    static string? Remainder(string text, UiSettings options)
    {
        var names = string.Join("|", Names(options).Select(LearnedNames.Pattern));
        if (names.Length == 0) return null;
        var match = Regex.Match(text.Trim(), @"^(?:(?:헤이|하이|hey)\s*)?(?:" + names + @")(?:[야아](?=\s|[~,.!?]|$))?[~,.!?\s]*", RegexOptions.IgnoreCase);
        return match.Success ? text.Trim()[match.Length..].Trim() : null;
    }
    public static WhisperWakeResult Decide(UiSettings options, string live, WhisperTranscript transcript)
    {
        var selected = WhisperText.Select(live, transcript.Text, transcript.Words);
        var rest = Remainder(selected, options);
        if (rest != null) return new(true, rest);
        var raw = WhisperText.Clean(live, transcript.Text);
        // A rejected transcript can establish a name only. Never run its invented remainder.
        if (raw.StartsWith("- ") || raw.Contains(" - ") || LearnedNames.Squash(raw).Length > LearnedNames.Squash(live).Length * 3 + 10) return new(false, "");
        return new(Remainder(raw, options) != null, "");
    }
}
