using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal static class WakePhraseChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid phrases accepted"); }
    internal static int Run(List<object> rows)
    {
        int failed = 0;
        void Check(string name, Action action) { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var original = new UiSettings { Name = "자비스", LearnedWakes = new() { ["자비스"] = ["자빗스"] } };
        var custom = original with { WakeWords = ["코코", "헤이 코코", "음"] };
        Check("Explicit wakes replace the default name and learned aliases; clearing restores both", () =>
        {
            Require(JsonSerializer.Deserialize<UiSettings>("{}")!.WakeWords.Length == 0, "Legacy defaults changed");
            Require(WakePhrases.Active(custom).SequenceEqual(custom.WakeWords) && !WakePhrases.Active(custom).Contains("자비스"), "Disabled name leaked");
            Require(LearnedNames.Strip("자비스 몇 시야", custom) == "자비스 몇 시야" && LearnedNames.Strip("자빗스 몇 시야", custom) == "자빗스 몇 시야", "Disabled name stripped");
            Require(LearnedNames.Strip("자비스 몇 시야", original) == "몇 시야" && LearnedNames.Strip("자빗스 몇 시야", original) == "몇 시야", "Default/learned wake not restored");
        });
        Check("Command stripping uses longest literal custom phrase and preserves non-prefix content", () =>
        {
            var options = original with { WakeWords = ["코      코", "코코 도와줘", "A+B", "음"] };
            Require(LearnedNames.Strip("코 코 도와줘, 몇 시야", options) == "몇 시야", "Short phrase shadowed a longer phrase");
            Require(LearnedNames.Strip("A+B 지금 몇 시야", options) == "지금 몇 시야" && LearnedNames.Strip("AAAB 몇 시야", options) == "AAAB 몇 시야", "Regex punctuation not literal");
            Require(LearnedNames.Strip("음 몇 시야", options) == "몇 시야" && LearnedNames.Strip("다른 말 코코 몇 시야", options) == "다른 말 코코 몇 시야", "One-character phrase or prefix boundary lost");
        });
        Check("Wake settings export/import preserve phrases and reject malformed lists before applying", () =>
        {
            var result = SettingsTransfer.Parse(SettingsTransfer.Serialize(custom));
            Require(result.WakeWords.SequenceEqual(custom.WakeWords), "Transfer lost phrases");
            var copy = SettingsTransfer.Copy(custom); copy.WakeWords[0] = "변경"; Require(custom.WakeWords[0] == "코코", "Transfer copy shared storage");
            foreach (var words in new string[][] { null!, [null!], [""], ["  "], ["!"], ["줄\n바꿈"], [new string('가', 41)], Enumerable.Repeat("코코", 33).ToArray() }) Reject(() => SettingsStore.Validate(original with { WakeWords = words }));
            SettingsStore.Validate(original with { WakeWords = ["음", "계산기 켜", "A+B"] });
            Require(SettingsTransfer.Describe(original, custom).Contains("별도 호출어"), "Import review hid custom activation");
        });
        Check("Mac custom wakes retain one-character and command-like phrases without merging with learned names", () =>
        {
            var imported = MacSettingsTransfer.Parse("{\"wakeWords\":[\"음\",\"계산기 켜\",\"헤이 코코\"]}", custom);
            Require(imported.Settings.WakeWords.SequenceEqual(new[] { "음", "계산기 켜", "헤이 코코" }) && imported.Settings.LearnedWakes["자비스"].SequenceEqual(original.LearnedWakes["자비스"]), "Mac phrase changed or became learned alias");
            Require(MacSettingsTransfer.Parse("{\"wakeWords\":[]}", custom).Settings.WakeWords.Length == 0, "Explicit empty Mac list did not restore name");
            Require(MacSettingsTransfer.Parse("{\"assistantName\":\"코코\"}", custom).Settings.WakeWords.SequenceEqual(custom.WakeWords), "Partial Mac import cleared omitted phrases");
            Require(imported.Notices.Any(n => n.Contains("목록")), "Override behavior hidden");
        });
        Check("Whisper candidate prompt and accepted remainder use only active custom phrases", () =>
        {
            var options = custom with { WhisperMode = "cpu", WakeWords = ["코코", "도와줘"] };
            Require(!WhisperWake.Candidate(options, "자비스", 1.2) && WhisperWake.Candidate(options, "도와 줘", 1.2), "Candidate used disabled name onset");
            Require(!WhisperWake.Candidate(options with { Name = "Sasarisa" }, "", 1.2) && WhisperWake.Candidate(options with { WakeWords = ["hello"] }, "", 1.2), "Empty Latin check used disabled name");
            Require(WhisperWake.WithPrompt(options).Text.CustomWords.SequenceEqual(options.WakeWords), "Prompt included disabled name");
            Require(WhisperWake.Decide(options, "코코 몇 시야", new("코코 몇 시야", Array.Empty<WhisperWord>())).Command == "몇 시야", "Custom remainder lost");
            Require(!WhisperWake.Decide(options, "자비스", new("자비스", Array.Empty<WhisperWord>())).Wake, "Whisper reenabled disabled name");
            var spaced = options with { WakeWords = ["코      코", "코코 도와줘"] };
            Require(WhisperWake.Decide(spaced, "코코 도와줘 몇 시야", new("코코 도와줘 몇 시야", Array.Empty<WhisperWord>())).Command == "몇 시야", "Whitespace caused a shorter Whisper wake to shadow the longer phrase");
        });
        Check("Speaker barge-in fallback uses active phrases while retaining explicit stop commands", () =>
        {
            var session = new BargeInSession(); session.Begin("오늘은 맑은 날입니다", original.Name, "wake", "Speakers", WakePhrases.Active(custom));
            Require(!session.AcceptText("자비스 들어봐", false) && !session.AcceptText("자빗스 들어봐", false), "Disabled name interrupted speech");
            Require(session.AcceptText("코코 들어봐", false) && session.AcceptText("음", false) && session.AcceptText("그만", false), "Active wake or stop lost"); session.End(true);
        });
        Check("Name training preserves explicit wake choices until the user clears the list", () =>
        {
            var training = new NameLearning(custom, "자비스"); for (int i = 0; i < NameLearning.TotalTakes; i++) training.Add(training.Prompt, "", true);
            var applied = training.ApplyTo(custom); Require(applied.WakeWords.SequenceEqual(custom.WakeWords) && custom.WakeWords[0] == "코코", "Learning overwrote custom activation");
        });
        Check("Wake cues show active choices and bound long lists without displaying a disabled name", () =>
        {
            var cue = WakePhrases.Cue(custom with { WakeWords = ["코코", "도와줘", "안녕", "음"] });
            Require(cue.Contains("코코") && cue.Contains("외 1개") && !cue.Contains("자비스"), "Status cue misleading");
            Require(WakePhrases.ReadInput(" 코코\r\n\n코코\n도와줘 ").SequenceEqual(new[] { "코코", "도와줘" }), "Editor did not normalize lines");
        });
        return failed;
    }
}
