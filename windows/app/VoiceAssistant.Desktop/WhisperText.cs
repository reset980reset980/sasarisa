using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

internal static class WhisperText
{
    // Original Mac policy: commands without dictated/search text need no second recognition pass.
    public static bool Quick(CommandPlan? plan) => plan?.Actions.Count > 0 && plan.Actions.All(x => x.Kind is
        "answer" or "greet" or "stop" or "media" or "seek" or "display_off" or "browser" or "volume" or "volume_delta" or
        "cancel_timers" or "list_timers" or "undo" or "history" or "close_owned" or "youtube_select" or "youtube_list" or "youtube_close");
    static string Comparable(string text)
    {
        foreach (var (latin, korean) in new[] { ("youtube", "유튜브"), ("claude", "클로드"), ("chrome", "크롬"), ("google", "구글"), ("github", "깃허브"), ("iphone", "아이폰"), ("blender", "블렌더"), ("ok", "오케이") })
            text = text.Replace(latin, korean, StringComparison.OrdinalIgnoreCase);
        return new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
    internal static string Clean(string live, string refined)
    {
        if (string.IsNullOrWhiteSpace(refined) || refined.Length > 6000) return "";
        var bad = new[] { "시청해 주셔서", "시청해주셔서", "구독과 좋아요", "구독 좋아요", "자막 제공", "MBC 뉴스", "Thanks for watching", "Thank you for watching", "Subtitles by" };
        return string.Join(" ", refined.Split('\n', '\r').Select(x => x.Trim()).Where(x => x.Length > 0 && !bad.Any(b => x.Contains(b, StringComparison.OrdinalIgnoreCase) && !live.Contains(b, StringComparison.OrdinalIgnoreCase))));
    }
    public static string Select(string live, string refined, IReadOnlyList<WhisperWord>? words = null)
    {
        var text = Clean(live, refined);
        if (text.Length == 0) return live;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var dropped = "";
        if (parts.Count >= 3)
        {
            var last = Comparable(parts[^1]);
            if (last.Length > 0 && Comparable(parts[^2]) == last && !Comparable(live).Contains(last))
            {
                dropped = last;
                while (parts.Count > 0 && Comparable(parts[^1]) == last) parts.RemoveAt(parts.Count - 1);
            }
            text = string.Join(" ", parts);
        }
        var sentences = Regex.Split(text, @"(?<=[.!?。])\s+");
        if (sentences.Length > 1 && sentences.Length % 2 == 0 && sentences.Take(sentences.Length / 2).Select(Comparable).SequenceEqual(sentences.Skip(sentences.Length / 2).Select(Comparable)))
            text = string.Join(" ", sentences.Take(sentences.Length / 2));
        var a = Comparable(live); var bText = Comparable(text);
        // Match the original confidence policy after removing repeated noise. Only words still
        // in the command count; a stripped wake name or discarded subtitle must not veto it.
        if (words != null && a.Length * 10 >= bText.Length * 7 && words.Any(w =>
        {
            var key = Comparable(w.Text);
            return w.Probability < 0.35 && key.Length >= 2 && key != dropped && bText.Contains(key) && !a.Contains(key);
        })) return live;
        if (bText.Length == 0 || text.StartsWith("- ") || text.Contains(" - ") || bText.Length > a.Length * 2 + 6) return live;
        if (a.Length >= 3 && a.Count(bText.Contains) / (double)a.Length < 0.45) return live;
        static double Korean(string s) => s.Length == 0 ? 0 : s.Count(c => c is >= '\uAC00' and <= '\uD7A3') / (double)s.Length;
        if (Korean(a) > 0.5 && Korean(bText) < 0.5) return live;
        return text;
    }
}
