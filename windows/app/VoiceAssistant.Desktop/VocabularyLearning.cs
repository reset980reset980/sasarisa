using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

public sealed record VocabularyCandidate(string Word, int Count, DateTimeOffset Seen);
public sealed record VocabularyUpdate(TextPreferences Preferences, string[] Added);

public static class VocabularyLearning
{
    const int MaxCandidates = 500;
    const string VerbEndings = "해줘요다고서면지까야어아게는은을를이가에도만의로와과랑봐죠";
    static readonly Regex Tokens = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    static IEnumerable<string> Words(string text) => Tokens.Matches(text).Select(x => x.Value);
    static string Key(string word) => word.ToLowerInvariant();
    public static bool IsTerm(string word) => !string.IsNullOrEmpty(word) && word.Length <= 60 && word.All(char.IsLetterOrDigit) && word.Any(char.IsLetter)
        && word.Length >= (word.Any(x => x is >= '가' and <= '힣') ? 2 : 3) && !VerbEndings.Contains(word[^1]);
    public static void Validate(Dictionary<string, VocabularyCandidate> candidates)
    {
        if (candidates == null || candidates.Count > MaxCandidates || candidates.Any(x => x.Value == null || !IsTerm(x.Value.Word) || x.Key != Key(x.Value.Word) || x.Value.Count is < 1 or > 2))
            throw new ArgumentException("단어 학습 기록을 확인하세요.");
    }
    public static VocabularyUpdate? Observe(TextPreferences preferences, string live, string refined, IEnumerable<string> names, DateTimeOffset now)
    {
        preferences.Validate();
        if (!preferences.AutoLearnWords || string.IsNullOrWhiteSpace(live) || string.IsNullOrWhiteSpace(refined) || live.Length > 6000 || refined.Length > 6000) return null;
        var known = preferences.CustomWords.Concat(names).SelectMany(Words).Select(Key).ToHashSet(StringComparer.Ordinal);
        var heard = Words(live).Select(Key).ToHashSet(StringComparer.Ordinal);
        var candidates = preferences.WordCandidates.Where(x => now - x.Value.Seen <= TimeSpan.FromDays(7) && x.Value.Seen <= now.AddMinutes(5) && !known.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var words = preferences.CustomWords.ToList(); var added = new List<string>();
        foreach (var word in Words(refined).Where(IsTerm).DistinctBy(Key))
        {
            var key = Key(word);
            if (known.Contains(key) || heard.Contains(key)) continue;
            var count = candidates.TryGetValue(key, out var candidate) ? candidate.Count + 1 : 1;
            if (count >= 3 && words.Count < 150) { words.Add(word); added.Add(word); known.Add(key); candidates.Remove(key); }
            else candidates[key] = new(word, Math.Min(count, 2), now);
        }
        candidates = candidates.OrderByDescending(x => x.Value.Seen).ThenByDescending(x => x.Value.Count).ThenBy(x => x.Key, StringComparer.Ordinal).Take(MaxCandidates).ToDictionary(x => x.Key, x => x.Value);
        if (added.Count == 0 && candidates.Count == preferences.WordCandidates.Count && candidates.All(x => preferences.WordCandidates.TryGetValue(x.Key, out var old) && old == x.Value)) return null;
        return new(preferences with { CustomWords = words.ToArray(), WordCandidates = candidates }, added.ToArray());
    }
}
