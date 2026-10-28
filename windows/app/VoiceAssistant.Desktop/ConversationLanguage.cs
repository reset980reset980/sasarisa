using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

public sealed record ConversationLanguage(string Code, string Label, string Locale, string EnglishName)
{
    // Keep the same language set and regional defaults as macOS Config.languages.
    public static readonly ConversationLanguage[] All =
    [
        new("ko", "한국어", "ko-KR", "Korean"), new("en", "English", "en-US", "English"),
        new("ja", "日本語", "ja-JP", "Japanese"), new("zh", "中文", "zh-CN", "Simplified Chinese"),
        new("es", "Español", "es-ES", "Spanish"), new("fr", "Français", "fr-FR", "French"),
        new("de", "Deutsch", "de-DE", "German"), new("pt", "Português", "pt-BR", "Brazilian Portuguese"),
        new("it", "Italiano", "it-IT", "Italian"), new("vi", "Tiếng Việt", "vi-VN", "Vietnamese"),
        new("th", "ไทย", "th-TH", "Thai"), new("id", "Bahasa Indonesia", "id-ID", "Indonesian")
    ];
    public static ConversationLanguage Get(string code) => All.FirstOrDefault(x => x.Code == code)
        ?? throw new ArgumentException("대화 언어를 지원 언어 목록에서 선택하세요.");
    public static string FromMac(string value)
    {
        if (value == null || !Regex.IsMatch(value, @"\A[a-zA-Z]{2}(?:[-_][a-zA-Z0-9]{2,8})*\z"))
            throw new ArgumentException("macOS 대화 언어 형식을 확인하세요.");
        return Get(value[..2].ToLowerInvariant()).Code;
    }
    public string SapiLanguage => CultureInfo.GetCultureInfo(Locale).LCID.ToString("x", CultureInfo.InvariantCulture);
    public string AnswerInstruction => "Answer naturally and briefly in " + EnglishName + ". Keep names, quoted text and tool arguments in the language required by the user's request.";
    public int SapiPriority(string attribute)
    {
        var requested = CultureInfo.GetCultureInfo(Locale).LCID;
        return attribute.Split(';').Select(x => int.TryParse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lcid)
            ? lcid == requested ? 2 : (lcid & 0x3ff) == (requested & 0x3ff) ? 1 : 0 : 0).DefaultIfEmpty(0).Max();
    }
    public bool MatchesSapi(string attribute) => SapiPriority(attribute) > 0;
}
