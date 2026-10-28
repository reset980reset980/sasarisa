using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceAssistant.Desktop;

internal static class WhisperWakeChecks
{
    public static int Run(List<object> rows)
    {
        int failed = 0;
        void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        void Check(string name, Action action) { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var options = new UiSettings { Name = "사사리사", WhisperMode = "cuda", Activation = "voice" };
        Check("Whisper wake accepts only short eligible name-like speech and Latin empty candidates", () =>
        {
            Require(WhisperWake.Candidate(options, "서서 있어", 1.2) && WhisperWake.Candidate(options, "자사리사", 1.2), "Similar onset rejected");
            Require(!WhisperWake.Candidate(options, "거기 있어", 1.2) && !WhisperWake.Candidate(options, "", 1.2), "Unrelated or empty Hangul candidate accepted");
            Require(WhisperWake.Candidate(options with { Name = "Sasarisa" }, "", 1.2), "Latin empty candidate lost");
            foreach (var seconds in new[] { 0, .5, 4.5, 100, double.NaN, double.PositiveInfinity }) Require(!WhisperWake.Candidate(options, "서서 있어", seconds), "Invalid duration accepted");
            foreach (var next in new[] { options with { Activation = "hold" }, options with { Activation = "hotkey" }, options with { WhisperMode = "off" }, options with { WhisperMode = "external", WhisperUrl = "" }, options with { Name = "" } }) Require(!WhisperWake.Candidate(next, "서서 있어", 1), "Disabled candidate accepted");
            Require(!WhisperWake.Candidate(options, new string('사', 25), 1), "Long conversation sent for idle check");
            foreach (var group in new[] { "가까카", "다따타", "바빠파", "사싸자짜차" }) Require(group.Select(c => WhisperWake.Onset(c.ToString())).Distinct().Count() == 1, "Onset group changed");
        });
        Check("Whisper wake trusts an accepted command but only the name from rejected text", () =>
        {
            const string live = "사서리사 오분 타이머"; const string refined = "사사리사 오분 타이머";
            var accepted = WhisperWake.Decide(options, live, new(refined, new[] { new WhisperWord("오분", .95) }));
            Require(accepted.Wake && accepted.Command == "오분 타이머", "Accepted command lost");
            var rejected = WhisperWake.Decide(options, live, new("사사리사 삼십분 타이머", new[] { new WhisperWord("삼십분", .1) }));
            Require(rejected.Wake && rejected.Command == "", "Rejected command executed");
            Require(WhisperWake.Decide(options with { Name = "Sasarisa" }, "", new("사사리사", Array.Empty<WhisperWord>())).Wake, "Korean reading of Latin name missed");
            foreach (var raw in new[] { "Thanks for watching 사사리사", "- 사사리사 - 계산기 켜", "나는 사사리사가 아닙니다", "사사리사 " + new string('가', 100), "거북아 오분 타이머" }) Require(!WhisperWake.Decide(options, "서서 있어", new(raw, Array.Empty<WhisperWord>())).Wake, "Unrelated text woke assistant: " + raw);
        });
        Check("Whisper wake name prompt is bounded and does not mutate saved vocabulary", () =>
        {
            var full = options with { Name = "Sasarisa", Text = new() { CustomWords = Enumerable.Range(0, 150).Select(i => "단어" + i).ToArray() } };
            var hinted = WhisperWake.WithPrompt(full);
            Require(hinted.Text.CustomWords.Length == 150 && hinted.Text.CustomWords.Take(2).SequenceEqual(new[] { "Sasarisa", "사사리사" }), "Name omitted when vocabulary is full");
            Require(full.Text.CustomWords[0] == "단어0" && full.Text.CustomWords.Length == 150, "Saved vocabulary overwritten");
            hinted.Text.Validate();
        });
        return failed;
    }
}
