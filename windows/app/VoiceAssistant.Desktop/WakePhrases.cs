using System;
using System.Linq;

namespace VoiceAssistant.Desktop;

internal static class WakePhrases
{
    public static void Validate(string[] words)
    {
        if (words == null || words.Length > 32) throw new ArgumentException("별도 호출어는 32개까지 입력할 수 있어요.");
        foreach (var word in words)
            if (string.IsNullOrWhiteSpace(word) || word.Length > 40 || word.Any(char.IsControl) || !word.Any(char.IsLetterOrDigit))
                throw new ArgumentException("별도 호출어는 한 줄에 하나씩, 글자나 숫자를 포함해 1–40자로 입력하세요.");
    }
    public static string[] ReadInput(string text)
    {
        var words = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Validate(words); return words;
    }
    // Explicit phrases replace the name and its learned pronunciations, as on macOS.
    public static string[] Active(UiSettings settings) => (settings.WakeWords.Length > 0 ? settings.WakeWords :
        new[] { settings.Name, settings.Language == "ko" ? NameLearning.HangulReading(settings.Name) ?? settings.Name : settings.Name }.Concat(LearnedNames.For(settings)).Concat(
            settings.Language != "ko" && LearnedNames.Squash(settings.Name) is "자비스" or "jarvis" ?
                settings.Language switch { "ja" => new[] { "ジャービス", "jarvis" }, "zh" => new[] { "贾维斯", "jarvis" }, _ => new[] { "jarvis" } } : Array.Empty<string>()))
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string Cue(UiSettings settings)
    {
        var words = settings.WakeWords.Length > 0 ? settings.WakeWords : new[] { settings.Name };
        return string.Join(" / ", words.Take(3).Select(x => "‘" + x + "’")) + (words.Length > 3 ? " 외 " + (words.Length - 3) + "개" : "");
    }
}
