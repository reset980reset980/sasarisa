using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

public sealed record NameTake(string Heard, string Refined, bool HadWake, string Command);
public sealed record NameLearningResult(string Name, int Total, int Before, int After, string[] Learned);

public static class LearnedNames
{
    public static string Squash(string text) => string.Concat(text.EnumerateRunes().Where(Rune.IsLetterOrDigit)).ToLowerInvariant();
    public static string Pattern(string word) => string.Join(@"\s*", word.EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r)).Select(r => Regex.Escape(r.ToString())));
    public static string[] For(UiSettings settings, string? name = null) =>
        settings.LearnedWakes.TryGetValue(name ?? settings.Name, out var values) ? values : Array.Empty<string>();
    public static bool Contains(string text, IEnumerable<string> words) => words.Any(word => Pattern(word).Length > 0 && Regex.IsMatch(text, Pattern(word), RegexOptions.IgnoreCase));
    public static string Strip(string text, UiSettings settings)
    {
        var words = For(settings).Append(settings.Name).OrderByDescending(x => Squash(x).Length);
        var pattern = string.Join("|", words.Select(Pattern).Where(x => x.Length > 0));
        if (pattern.Length == 0) return text.Trim();
        return Regex.Replace(text.Trim(), @"^(?:(?:헤이|하이|hey)\s*)?(?:" + pattern + @")(?:[야아](?=\s|[~,.!?]|$))?[~,.!?\s]*", "", RegexOptions.IgnoreCase).Trim();
    }
    public static void Validate(Dictionary<string, string[]> values)
    {
        if (values == null || values.Count > 32) throw new ArgumentException("이름 보정은 32개 이름까지 저장할 수 있어요.");
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 20 || pair.Key.Any(char.IsControl) || pair.Value == null || pair.Value.Length > 32)
                throw new ArgumentException("저장된 이름 보정의 이름과 개수를 확인하세요.");
            foreach (var word in pair.Value)
                if (word == null || word.Length > 40 || word.Any(char.IsControl) || !NameLearning.Usable(word, 2) || !word.EnumerateRunes().All(r => Rune.IsLetterOrDigit(r) || Rune.IsWhiteSpace(r)))
                    throw new ArgumentException("이름 보정은 명령이 아닌 2~12글자의 발음만 사용할 수 있어요.");
        }
    }
}

public sealed class NameLearning
{
    public const int TotalTakes = 12;
    static readonly string[] Commands = { "유튜브 틀어", "소리 줄여", "지금 몇 시야", "타이머 3분" };
    static readonly HashSet<string> Common = new(new[] { "그래서", "아니야", "아니요", "뭐라고", "잠깐만", "있잖아", "그러니까", "알겠어", "고마워", "괜찮아" });
    readonly UiSettings original;
    readonly List<NameTake> takes = new();
    public string Name { get; }
    public IReadOnlyList<NameTake> Takes => takes;
    public bool Finished => takes.Count >= TotalTakes;
    public NameLearning(UiSettings settings, string name) { original = SettingsTransfer.Copy(settings); Name = name.Trim(); }
    public string Command => takes.Count < 8 ? "" : Commands[Math.Min(3, takes.Count - 8)];
    public string Prompt => Name + (Command.Length > 0 ? ", " + Command : "");
    public bool Add(string heard, string refined, bool hadWake)
    {
        if (Finished || heard.Length > 500 || refined.Length > 500 || LearnedNames.Squash(heard + refined).Length == 0) return false;
        takes.Add(new(heard, refined, hadWake, Command)); return true;
    }
    public void RetryLast() { if (takes.Count > 0) takes.RemoveAt(takes.Count - 1); }
    bool Hit(NameTake take) => take.HadWake || LearnedNames.Contains(take.Heard, LearnedNames.For(original, Name).Append(Name)) ||
        LearnedNames.Contains(take.Refined, LearnedNames.For(original, Name).Append(Name));
    public NameLearningResult Result()
    {
        var before = takes.Count(Hit); var seen = new Dictionary<string, int>();
        var target = HangulReading(Name) ?? Name;
        var minimum = Math.Max(2, Math.Min(3, LearnedNames.Squash(target).Length));
        foreach (var take in takes.Where(t => !Hit(t)))
            foreach (var candidate in new[] { take.Heard, take.Refined }.Select(Normalize).Select(t => WithoutCommand(t, take.Command)).Where(t => t != null).Distinct())
                if (Usable(candidate!, minimum)) seen[candidate!] = seen.GetValueOrDefault(candidate!) + 1;
        var learned = LearnedNames.For(original, Name).Concat(seen.Where(p => LearnedNames.Squash(p.Key).Length >= 3 ? p.Value >= 2 || Close(p.Key, target) : p.Value >= 2 && Close(p.Key, target)).Select(p => p.Key))
            .Distinct().OrderBy(x => x, StringComparer.Ordinal).Take(32).ToArray();
        var after = takes.Count(t => Hit(t) || LearnedNames.Contains(t.Heard, learned) || LearnedNames.Contains(t.Refined, learned));
        return new(Name, takes.Count, before, after, learned);
    }
    public UiSettings ApplyTo(UiSettings settings)
    {
        if (!Finished) throw new InvalidOperationException("12번의 시험을 마친 뒤 적용하세요.");
        var next = SettingsTransfer.Copy(settings); next.Name = Name; next.LearnedWakes[Name] = Result().Learned;
        SettingsStore.Validate(next); return next;
    }
    public static string Normalize(string text) => Regex.Replace(Regex.Replace(text, @"[~∼〜!?.,]+", " ").ToLowerInvariant().Trim(), @"\s+", " ");
    public static string? WithoutCommand(string text, string command)
    {
        if (command.Length == 0) return text;
        var all = LearnedNames.Squash(text); var tail = LearnedNames.Squash(command);
        if (!all.EndsWith(tail, StringComparison.Ordinal) || all.Length <= tail.Length) return null;
        var remaining = all.EnumerateRunes().Count() - tail.EnumerateRunes().Count(); var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes()) { if (remaining == 0) break; result.Append(rune); if (Rune.IsLetterOrDigit(rune)) remaining--; }
        return Normalize(result.ToString());
    }
    public static bool Usable(string text, int minimum)
    {
        var word = LearnedNames.Squash(text);
        return word.Length >= minimum && word.Length <= 12 && text.EnumerateRunes().All(r => Rune.IsLetterOrDigit(r) || Rune.IsWhiteSpace(r)) && !Common.Contains(word) && CommandParser.Parse(text) == null;
    }
    static int[] Jamo(string text) => LearnedNames.Squash(text).EnumerateRunes().SelectMany(r =>
    {
        var v = r.Value - 0xAC00;
        if (v < 0 || v >= 11172) return new[] { r.Value };
        var first = new[] { 0x1100 + v / 588, 0x1161 + v % 588 / 28 };
        return v % 28 > 0 ? first.Append(0x11A7 + v % 28).ToArray() : first;
    }).ToArray();
    public static bool Close(string a, string b)
    {
        var x = Jamo(a); var y = Jamo(b); if (x.Length == 0 || y.Length == 0) return false;
        var row = Enumerable.Range(0, y.Length + 1).ToArray();
        for (var i = 1; i <= x.Length; i++)
        {
            var previous = row[0]; row[0] = i;
            for (var j = 1; j <= y.Length; j++) { var current = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (x[i - 1] == y[j - 1] ? 0 : 1)); previous = current; }
        }
        return row[y.Length] <= Math.Max(2, (int)(Math.Max(x.Length, y.Length) * .4));
    }
    static string? HangulReading(string name)
    {
        var value = name.ToLowerInvariant(); if (!Regex.IsMatch(value, @"^([bcdfghjklmnprstvz]?[aeiouy])+$")) return null;
        var leads = new Dictionary<char, int> { ['g']=0,['n']=2,['d']=3,['r']=5,['l']=5,['m']=6,['b']=7,['v']=7,['s']=9,['j']=12,['z']=12,['c']=15,['k']=15,['t']=16,['p']=17,['f']=17,['h']=18 };
        var vowels = new Dictionary<char, int> { ['a']=0,['e']=5,['i']=20,['y']=20,['o']=8,['u']=13 };
        var result = new StringBuilder(); var lead = 11;
        foreach (var ch in value) { if (vowels.TryGetValue(ch, out var vowel)) { result.Append((char)(0xAC00 + (lead * 21 + vowel) * 28)); lead = 11; } else lead = leads.GetValueOrDefault(ch, 11); }
        return result.ToString();
    }
}
