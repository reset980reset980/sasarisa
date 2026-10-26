using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal static class NameLearningChecks
{
    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    public static int Run(List<object> rows)
    {
        var failed = 0;
        void Check(string name, Action action)
        { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        Check("Name training requires eight names and four commands; empty takes do not count", () =>
        {
            var learning = new NameLearning(new(), "사사리사");
            Require(!learning.Add("", "...", false) && learning.Takes.Count == 0, "Silence counted");
            for (var i = 0; i < 8; i++) { Require(learning.Command == "", "Command prompted too early"); learning.Add("서서 있어", "", false); }
            Require(learning.Command == "유튜브 틀어", "First command missing");
            for (var i = 0; i < 4; i++) learning.Add("서서 있어 " + learning.Command, "", false);
            var result = learning.Result();
            Require(learning.Finished && result.Total == 12 && result.Before == 0 && result.After == 12 && result.Learned.SequenceEqual(new[] { "서서 있어" }), "Training result lost phrase or learned a command");
            Require(!learning.Add("extra", "", false), "Extra take accepted");
            learning.RetryLast(); Require(!learning.Finished && learning.Command == "타이머 3분", "Retry advanced prompt");
        });
        Check("Name training learns repeated unusual readings but not a single unrelated word", () =>
        {
            var learning = new NameLearning(new(), "사사리사");
            learning.Add("하늘하늘", "하늘하늘", false);
            Require(learning.Result().Learned.Length == 0, "One take counted twice across engines");
            learning.Add("하늘하늘", "", false);
            Require(learning.Result().Learned.Contains("하늘하늘"), "Repeated reading not learned");
            Require(NameLearning.Close("재비쓰", "자비스") && !NameLearning.Close("재바쓰", "자비스"), "Hangul phonetic distance boundary failed");
        });
        Check("Two-syllable learned names must be repeated and phonetically close", () =>
        {
            var learning = new NameLearning(new(), "코코"); learning.Add("토토", "", false);
            Require(learning.Result().Learned.Length == 0, "Single short reading learned");
            learning.Add("토토", "", false); Require(learning.Result().Learned.Contains("토토"), "Repeated close short reading ignored");
            learning.Add("학교", "", false); learning.Add("학교", "", false);
            Require(!learning.Result().Learned.Contains("학교"), "Unrelated short reading learned");
        });
        Check("Commands and common dialogue cannot become wake aliases", () =>
        {
            foreach (var word in new[] { "그만", "타이머 3분", "유튜브 틀어", "고마워", "아니요", "잠깐만", "네", "아주아주아주아주아주아주길다", "재.*비" })
                Require(!NameLearning.Usable(word, 2), "Unsafe or common alias: " + word);
            Require(NameLearning.WithoutCommand("서서 있어 유튜브 틀어", "유튜브 틀어") == "서서 있어", "Command tail retained");
            Require(NameLearning.WithoutCommand("유튜브 틀어", "유튜브 틀어") == null && NameLearning.WithoutCommand("서서 있어 브라우저 열어", "유튜브 틀어") == null, "Missing command boundary accepted");
        });
        Check("Applying learned names preserves other profiles and never mutates the current settings", () =>
        {
            var original = new UiSettings { Device = "1", LearnedWakes = new() { ["다른이름"] = new[] { "다른발음" } } };
            var learning = new NameLearning(original, "사사리사");
            try { learning.ApplyTo(original); throw new Exception("Incomplete training saved"); } catch (InvalidOperationException) { }
            for (var i = 0; i < 12; i++) learning.Add("서서 있어 " + learning.Command, "", false);
            var applied = learning.ApplyTo(original); var restored = SettingsTransfer.Parse(SettingsTransfer.Serialize(applied));
            Require(original.Name == "자비스" && original.LearnedWakes.Count == 1, "Existing settings changed before commit");
            Require(restored.Name == "사사리사" && restored.LearnedWakes["다른이름"][0] == "다른발음" && restored.LearnedWakes["사사리사"].Contains("서서 있어"), "Export lost name profiles");
            Require(SettingsTransfer.Describe(original, restored).Contains("서서 있어"), "Import preview hides wake changes");
        });
        Check("Learned wake stripping retains the command and supports spaced recognition", () =>
        {
            var settings = new UiSettings { Name = "사사리사", LearnedWakes = new() { ["사사리사"] = new[] { "서서 있어", "서서" } } };
            Require(LearnedNames.Strip("헤이 서 서 있 어, 타이머 3분", settings) == "타이머 3분", "Alias prefix was truncated or command lost");
            Require(LearnedNames.Strip("사 사 리 사야 메모장 열어", settings) == "메모장 열어", "Base name no longer stripped");
            Require(LearnedNames.Strip("지금 몇 시야", settings) == "지금 몇 시야", "Unrelated command changed");
            var symbols = new UiSettings { Name = "C++" };
            Require(LearnedNames.Strip("C 드라이브 열어", symbols) == "C 드라이브 열어" && LearnedNames.Strip("C++ 메모장 열어", symbols) == "메모장 열어", "Punctuation in the configured name was discarded");
            Require(!LearnedNames.Contains("C 드라이브 열어", new[] { "C++" }), "A prefix was counted as the whole name");
            Require(JsonSerializer.Deserialize<UiSettings>("{}")!.LearnedWakes.Count == 0, "Old settings have no default");
            try { SettingsStore.Validate(settings with { LearnedWakes = new() { ["사사리사"] = new[] { "그만" } } }); throw new Exception("Command alias accepted"); } catch (ArgumentException) { }
        });
        return failed;
    }
}
