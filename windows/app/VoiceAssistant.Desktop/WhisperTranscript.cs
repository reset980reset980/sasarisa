using System;
using System.Collections.Generic;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal sealed record WhisperWord(string Text, double Probability);
internal sealed record WhisperTranscript(string Text, IReadOnlyList<WhisperWord> Words)
{
    public static WhisperTranscript Empty { get; } = new("", Array.Empty<WhisperWord>());

    public static WhisperTranscript Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException("음성 보정 응답에 문장이 없어요.");
        var text = value.GetString()!.Trim();
        if (text.Length > 6000) throw new JsonException("음성 보정 문장이 너무 길어요.");
        var words = new List<WhisperWord>();
        // Older compatible servers may return text only. Never invent confidence for those replies.
        if (!root.TryGetProperty("segments", out var segments)) return new(text, words);
        if (segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() > 512)
            throw new JsonException("음성 보정 구간을 읽을 수 없어요.");
        int pieces = 0, characters = 0;
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) throw new JsonException("음성 보정 구간을 읽을 수 없어요.");
            if (!segment.TryGetProperty("words", out var tokens)) continue;
            if (tokens.ValueKind != JsonValueKind.Array) throw new JsonException("음성 보정 단어를 읽을 수 없어요.");
            foreach (var token in tokens.EnumerateArray())
            {
                if (++pieces > 12000 || token.ValueKind != JsonValueKind.Object ||
                    !token.TryGetProperty("word", out var word) || word.ValueKind != JsonValueKind.String ||
                    !token.TryGetProperty("probability", out var probability) || probability.ValueKind != JsonValueKind.Number ||
                    !probability.TryGetDouble(out var p) || !double.IsFinite(p) || p < 0 || p > 1)
                    throw new JsonException("음성 보정 단어의 확률을 읽을 수 없어요.");
                var piece = word.GetString()!; characters += piece.Length;
                if (characters > 24000) throw new JsonException("음성 보정 단어가 너무 길어요.");
                if (piece.Length == 0) continue;
                // whisper.cpp words are token pieces. A word is only as certain as its weakest piece.
                if (char.IsWhiteSpace(piece[0]) || words.Count == 0) words.Add(new(piece.Trim(), p));
                else words[^1] = new(words[^1].Text + piece, Math.Min(words[^1].Probability, p));
            }
        }
        return new(text, words);
    }
}
