using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

internal sealed record MultilingualDecision(bool Accepted, bool Wake, string Text);
internal static class MultilingualSpeech
{
    internal static readonly ModelFile SpeechDetector = new("silero_vad.onnx", 643854, "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6");
    public static string? AfterWake(string text, UiSettings options)
    {
        text = text.Normalize(NormalizationForm.FormC);
        foreach (var name in WakePhrases.Active(options).OrderByDescending(x => x.Length))
        {
            var normalized = name.Normalize(NormalizationForm.FormC); var last = normalized.EnumerateRunes().Last();
            var latin = last.Value <= 0x024f && Rune.IsLetterOrDigit(last);
            var pattern = @"\A(?:(?:hey|hi|hello|헤이|하이)\s+)?" + LearnedNames.Pattern(normalized) +
                (latin ? @"(?=$|[^\p{L}\p{N}\p{M}])" : "") + @"(?:[야아](?=\s|[,.!?~]|$))?[\s,.!?~、。！？]*";
            var match = Regex.Match(text.Trim(), pattern, RegexOptions.IgnoreCase);
            if (match.Success) return text.Trim()[match.Length..].Trim();
        }
        return null;
    }
    public static MultilingualDecision Decide(UiSettings options, WhisperTranscript transcript, bool session, bool dictation)
    {
        var text = WhisperText.Clean("", transcript.Text);
        // This is primary recognition: there is no independent transcript to fall back to.
        if (text.Length == 0 || transcript.Words.Any(x => x.Text.Any(char.IsLetterOrDigit) && x.Probability < .35)) return new(false, false, "");
        if (dictation) return new(session, false, session ? text : "");
        var command = AfterWake(text, options);
        var wake = options.Activation == "voice" && command != null;
        return new(session || wake, wake, session || wake ? command ?? text : "");
    }
    public static void ValidateInput(UiSettings settings)
    {
        var language = ConversationLanguage.Get(settings.Language);
        if (language.Code != "ko" && !WhisperEngine.Enabled(settings))
            throw new ArgumentException("다국어 음성 입력에는 로컬 Whisper 엔진이 필요해요. 설정에서 CPU·NVIDIA 엔진을 준비하거나 로컬 서버 주소를 입력하세요.");
    }
}
