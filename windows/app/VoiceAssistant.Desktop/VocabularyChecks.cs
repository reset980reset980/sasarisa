using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal static class VocabularyChecks
{
    public static int Run(List<object> rows)
    {
        int failed = 0; var now = DateTimeOffset.UtcNow;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid vocabulary accepted"); }
        void Check(string name, Action action)
        { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        Check("Vocabulary requires three different utterances and survives serialized restart", () =>
        {
            var prefs = new TextPreferences { AutoLearnWords = true };
            for (int i = 0; i < 3; i++)
            {
                var update = VocabularyLearning.Observe(prefs, "알파 찾기", "알파카 알파카 ALPACA alpaca 찾기", Array.Empty<string>(), now.AddSeconds(i))!;
                Require(update.Added.Length == (i == 2 ? 2 : 0), "Repeated word within one utterance counted twice");
                prefs = JsonSerializer.Deserialize<TextPreferences>(JsonSerializer.Serialize(update.Preferences))!;
            }
            Require(prefs.CustomWords.SequenceEqual(new[] { "알파카", "ALPACA" }) && prefs.WordCandidates.Count == 0, "Learning or casing lost");
        });
        Check("Vocabulary skips disabled learning silence known words wake names and Korean verb endings", () =>
        {
            var prefs = new TextPreferences { CustomWords = new[] { "블렌더" }, AutoLearnWords = true };
            Require(VocabularyLearning.Observe(prefs with { AutoLearnWords = false }, "기존", "새단어", Array.Empty<string>(), now) == null, "Disabled learned");
            Require(VocabularyLearning.Observe(prefs, "", "새단어", Array.Empty<string>(), now) == null, "Silence learned");
            Require(VocabularyLearning.Observe(prefs, "알파카", "알파카 블렌더 자비스 실행해 보여줘 좋아요 12345 a ab", new[] { "자비스" }, now) == null, "Non-term or known word learned");
        });
        Check("Vocabulary preserves manual words at capacity and bounds candidate age and count", () =>
        {
            var words = Enumerable.Range(0,150).Select(x => "manual" + x).ToArray();
            var prefs = new TextPreferences { AutoLearnWords = true, CustomWords = words, WordCandidates = new() { ["알파카"] = new("알파카", 2, now), ["expired"] = new("expired", 2, now.AddDays(-8)) } };
            var update = VocabularyLearning.Observe(prefs, "이전 단어", "알파카", Array.Empty<string>(), now)!;
            Require(update.Added.Length == 0 && update.Preferences.CustomWords.SequenceEqual(words) && !update.Preferences.WordCandidates.ContainsKey("expired"), "Manual word evicted or stale candidate survived");
            var many = VocabularyLearning.Observe(new() { AutoLearnWords = true }, "이전 단어", string.Join(" ", Enumerable.Range(0,510).Select(x => "term" + x)), Array.Empty<string>(), now)!;
            Require(many.Preferences.WordCandidates.Count == 500, "Unbounded candidates");
        });
        Check("Vocabulary rejects corrupt candidates and excludes them from settings export", () =>
        {
            Reject(() => new TextPreferences { WordCandidates = null! }.Validate());
            Reject(() => SettingsTransfer.Serialize(new() { Text = null! }));
            Reject(() => new TextPreferences { WordCandidates = new() { ["broken"] = new(null!, 2, now) } }.Validate());
            Reject(() => new TextPreferences { WordCandidates = new() { ["알파카"] = new("알파카", 3, now) } }.Validate());
            var settings = new UiSettings { Text = new() { AutoLearnWords = true, WordCandidates = new() { ["privatecandidate"] = new("privatecandidate", 1, now) } } };
            var exported = SettingsTransfer.Serialize(settings);
            Require(!exported.Contains("privatecandidate") && SettingsTransfer.Parse(exported).Text.AutoLearnWords, "Export leaked candidate or lost option");
            Require(SettingsTransfer.Prepare(new(), settings, false, false).Text.WordCandidates.ContainsKey("privatecandidate"), "Import erased local progress");
        });
        Check("Vocabulary imports Handy Mac Windows and text formats without unrelated settings", () =>
        {
            foreach (var source in new[] { "{\"settings\":{\"custom_words\":[\"블렌더\"],\"custom_filler_words\":[\"음\"]}}", "{\"custom_words\":[\"블렌더\"],\"custom_filler_words\":[\"음\"]}", "{\"customWords\":[\"블렌더\"],\"fillerWords\":[\"음\"],\"freeMode\":true}", "{\"Text\":{\"CustomWords\":[\"블렌더\"],\"FillerWords\":[\"음\"]},\"WhisperUrl\":\"https://ignored.invalid\"}", VocabularyFiles.Serialize(new() { CustomWords = new[] { "블렌더" }, FillerWords = new[] { "음" } }) })
            { var list = VocabularyFiles.Parse(source, true); Require(list.Words.Single() == "블렌더" && list.Fillers.Single() == "음", "Format not read"); }
            var plain = VocabularyFiles.Parse("  Blender\r\nblender\n\n블렌더 ", false); Require(plain.Words.SequenceEqual(new[] { "Blender", "블렌더" }), "Text dedup failed");
            Require(VocabularyFiles.Parse(SettingsTransfer.Serialize(new() { Text = new() { CustomWords = new[] { "블렌더" } } }), true).Words.Single() == "블렌더", "Settings package not read");
        });
        Check("Vocabulary import rejects malformed duplicate oversized and excessive word lists", () =>
        {
            foreach (var bad in new[] { "{", "{}", "{\"Text\":null}", "{\"customWords\":null}", "{\"customWords\":[1]}", "{\"customWords\":[],\"customWords\":[]}", "{\"customWords\":[\"bad\\nword\"]}", "{\"Format\":\"Sasarisa.Windows.Vocabulary\",\"Version\":2,\"Words\":[]}" }) Reject(() => VocabularyFiles.Parse(bad, true));
            Reject(() => VocabularyFiles.Parse(new string('x', VocabularyFiles.MaxBytes + 1), false));
            Reject(() => VocabularyFiles.Parse(string.Join("\n", Enumerable.Range(0,151).Select(x => "word" + x)), false));
        });
        Check("Vocabulary merge replace and rollback preserve unrelated preferences", () =>
        {
            var prefs = new TextPreferences { CustomWords = new[] { "기존단어" }, FillerWords = new[] { "음" }, AutoLearnWords = true, PolishDictation = true };
            var imported = new VocabularyList(new[] { "새단어" }, new[] { "어어" });
            var merged = VocabularyFiles.Apply(prefs, imported, false, false);
            Require(merged.CustomWords.SequenceEqual(new[] { "기존단어", "새단어" }) && merged.FillerWords.SequenceEqual(prefs.FillerWords) && merged.AutoLearnWords && merged.PolishDictation, "Merge changed unrelated choice");
            var replaced = VocabularyFiles.Apply(merged, new(prefs.CustomWords, prefs.FillerWords), true, true);
            Require(replaced.CustomWords.SequenceEqual(prefs.CustomWords) && replaced.FillerWords.SequenceEqual(prefs.FillerWords), "Explicit rollback failed");
        });
        Check("Vocabulary export can be reopened and keeps the prior export as a backup", () =>
        {
            var directory = Path.Combine(AppPaths.WindowsRoot, "artifacts", "vocabulary-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var file = Path.Combine(directory, "words.json");
            VocabularyFiles.Write(file, new() { CustomWords = new[] { "블렌더" } }); VocabularyFiles.Write(file, new() { CustomWords = new[] { "알파카" } });
            Require(VocabularyFiles.Read(file).Words.Single() == "알파카" && VocabularyFiles.Read(file + ".previous").Words.Single() == "블렌더", "Export backup not recoverable");
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "Export left temporary files");
        });
        return failed;
    }
}
