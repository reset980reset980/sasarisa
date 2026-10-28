using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceAssistant.Desktop;

internal static class EnglishCommandChecks
{
    internal static int Run(List<object> rows)
    {
        int failed = 0;
        void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
        void Check(string name, Action run) { try { run(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        AssistantAction Parse(string text)
        {
            var plan = CommandParser.Parse(text) ?? throw new Exception("Not parsed: " + text);
            Require(plan.Actions.Count == 1, "Expected one action: " + text);
            var action = plan.Actions.Single(); ActionPolicy.Validate(action); return action;
        }
        void Is(string text, string kind, string? value = null, int? number = null)
        {
            var action = Parse(text);
            Require(action.Kind == kind && (value == null || action.Value == value) && (number == null || action.Number == number), "Unexpected action for " + text + ": " + action);
        }
        Check("Original English clock date and stopping commands stay local", () =>
        {
            foreach (var text in new[] { "What time is it?", "tell me the time", "Please tell me the time.", "What's the date?", "What day is it?" })
            { Is(text, "answer"); Require(CommandParser.Parse(text)!.Reply.Length > 8, "Empty clock response"); }
            foreach (var text in new[] { "stop", "wait", "hold on", "that's all", "that's all please", "stop listening", "good night", "nevermind", "okay stop" }) Is(text, "stop");
        });
        Check("English timers parse complete numeric word and compound durations", () =>
        {
            foreach (var row in new (string Text, int Seconds)[] { ("3 minute timer", 180), ("set a timer for 10 minutes", 600), ("timer for 5secs", 5), ("set timer for an hour", 3600), ("one minute timer", 60), ("eighteen seconds timer", 18), ("twenty-one minute timer", 1260), ("set a timer for 1 hour and 2 minutes 3 seconds", 3723), ("half minute timer", 30), ("timer for 1.5 minutes", 90), ("timer for 24 hours", 86400) })
                Is(row.Text, "timer", "Timer", row.Seconds);
            Is("remind me in twenty minutes to stretch", "timer", "stretch", 1200);
            Is("remind me in 1 minute to check if the door is locked", "timer", "check if the door is locked", 60);
        });
        Check("Invalid English timer bounds never schedule a partial duration", () =>
        {
            foreach (var text in new[] { "timer for 0 seconds", "timer for 25 hours", "timer for 999999 hours", "timer for 0.001 seconds" })
                Require(CommandParser.Parse(text) is { Actions.Count: 0 }, "Out of range timer executed: " + text);
            foreach (var text in new[] { "timer for -5 minutes", "timer for 2 minutes later 10 hours", "timer for 2 minutes garbage", "timer for 1.2.3 seconds", "timer for 1000000 seconds" })
                Require(CommandParser.Parse(text) == null, "Incomplete duration executed: " + text);
            Is("list timers", "list_timers"); Is("cancel all timers", "cancel_timers");
        });
        Check("English volume output mute and media navigation retain distinct actions", () =>
        {
            Is("set the volume to 35 percent", "volume", number: 35); Is("volume 100%", "volume", number: 100); Is("make sound comfortable", "volume", number: 50);
            Is("turn volume up a little", "volume_delta", number: 7); Is("volume down a lot", "volume_delta", number: -30); Is("turn down the volume", "volume_delta", number: -15);
            Require(CommandParser.Parse("volume 101") is { Actions.Count: 0 }, "Invalid output level accepted");
            Is("mute", "media", "mute_on"); Is("unmute", "media", "unmute"); Is("pause music", "media", "pause"); Is("resume", "media", "play"); Is("next track", "media", "next");
            Is("go back", "browser", "back"); Is("go forward", "browser", "forward"); Is("refresh", "browser", "refresh");
            Is("rewind 2 minutes", "seek", number: -120); Is("fast forward", "seek", number: 10); Is("turn off the screen", "display_off");
        });
        Check("Literal English typing preserves punctuation case and explicit quoted data", () =>
        {
            Is("type Hello, World!", "type", "Hello, World!"); Is("write Why?", "type", "Why?");
            Is("type \"if it rains, then cancel that\"", "type", "if it rains, then cancel that");
            Is("type ‘Hello’!", "type", "‘Hello’!"); Is("write 'if it rains'", "type", "if it rains");
            Is("type hello, never mind", "stop"); Is("open chrome, scratch that", "stop");
        });
        Check("English conditionals and multiple actions never fall back to partial legacy execution", () =>
        {
            foreach (var text in new[] { "type hello and send it", "open chrome if it is closed", "youtube cats then play the second result", "play rain on youtube and close chrome", "set timer for 5 minutes then open notes", "search for cats and delete the first result", "youtube cats and type hello", "youtube rain and resume music", "search for cats and cancel timers" })
                Require(CommandParser.RequiresObservations(text) && CommandParser.Parse(text) == null, "Partial action: " + text);
        });
        Check("English search terms stay encoded data and shopping opens results only", () =>
        {
            Is("search google for cats & dogs", "open_url", "https://www.google.com/search?q=cats%20%26%20dogs");
            var amazon = Parse("buy a keyboard on amazon"); Require(amazon.Kind == "open_url" && new Uri(amazon.Value).Host.EndsWith("amazon.com") && amazon.Value.Contains("keyboard"), "Shopping became a purchase");
            var quoted = Parse("search for \"when and if\""); Require(quoted.Value.Contains("when%20and%20if"), "Quoted query became a condition");
            var session = new WebSearchSession(); var previous = CommandParser.Parse("search amazon for keyboards", session)!.Actions.Single(); session.Opened(previous.Value);
            Require(new Uri(CommandParser.Parse("search for silent switches", session)!.Actions.Single().Value).Host == new Uri(previous.Value).Host, "Search context was lost");
            Require(CommandParser.Parse("search for " + new string('x', 501)) is { Actions.Count: 0 }, "Oversize query accepted");
        });
        Check("English YouTube requests distinguish results playback and Korean hybrid inputs", () =>
        {
            Is("play rain sounds on youtube", "youtube_play", "rain sounds"); Is("youtube search for rain sounds", "youtube_search", "rain sounds");
            Is("play the second video", "youtube_select", number: 2); Is("tenth result", "youtube_select", number: 10);
            Is("youtube 에스파 틀어", "youtube_play", "에스파"); Is("유튜브 검색결과", "youtube_list");
        });
        Check("English platform app aliases preserve known Windows targets", () =>
        {
            Is("open chrome", "open_app", "chrome"); Is("open notes", "open_app", "notepad"); Is("open textedit", "open_app", "notepad"); Is("open finder", "open_app", "explorer"); Is("open file explorer", "open_app", "explorer");
            Require(CommandParser.Parse("open safari") == null, "Unsupported app silently substituted");
        });
        Check("English coding handoff keeps task data and existing session commands separate", () =>
        {
            Is("ask Claude to fix the login bug if tests fail", "delegate", "fix the login bug if tests fail");
            Is("have Codex write tests", "delegate_codex", "write tests");
            Require(CommandParser.Parse("tell Claude to fix the login bug") == null, "Existing session became a new CLI session");
            Require(ActionPolicy.NeedsConfirmation(Parse("ask Claude to fix the bug"), false), "Handoff lost its consent policy");
        });
        Check("English dictation controls do not consume ordinary dictated sentences", () =>
        {
            Is("take dictation", "dictation"); Is("make this polite", "rewrite", "make this polite"); Is("translate this to English", "rewrite"); Is("undo that", "undo"); Is("what did you just do", "history");
            foreach (var text in new[] { "stop dictation", "Jarvis end dictation", "finish dictation" }) Require(DictationUtterance.Parse(text, "Jarvis").Control == DictationControl.Stop, "Missing stop control");
            Require(DictationUtterance.Parse("we can stop dictation later", "Jarvis").Control == DictationControl.Text, "Ordinary dictation consumed");
        });
        Check("Korean clock timers app volume and explicit unmute remain local", () =>
        {
            Is("지금 몇시야", "answer"); Is("5분 타이머", "timer", number: 300); Is("메모장 열어", "open_app", "notepad"); Is("소리 줄여", "volume_delta", number: -10);
            Is("음소거", "media", "mute_on"); Is("음소거 해제", "media", "unmute");
            ActionPolicy.Validate(new("media", "mute"));
        });
        Check("Explicit mute and unmute are idempotent and reversible without touching real audio", () =>
        {
            var device = new FakeDevice(); var output = new OutputMute(() => device);
            output.Set(false); Require(device.Writes == 0, "Unmute toggled an unmuted output");
            var change = output.Set(true); output.Set(true); Require(device.Muted && device.Writes == 1, "Repeated mute toggled output");
            output.Restore(change); Require(!device.Muted && device.Writes == 2, "Mute undo did not restore");
            device.Muted = true; var unmute = output.Set(false); output.Set(false); Require(!device.Muted, "Repeated unmute toggled output"); output.Restore(unmute); Require(device.Muted, "Unmute undo failed");
        });
        Check("Mute undo refuses changed output device or a later manual state", () =>
        {
            var device = new FakeDevice(); var output = new OutputMute(() => device); var change = output.Set(true); device.Id = "other";
            void Refuse() { var writes = device.Writes; try { output.Restore(change); } catch (InvalidOperationException) { Require(device.Writes == writes, "Rejected undo wrote output"); return; } throw new Exception("Changed output overwritten"); }
            Refuse(); device.Id = change.Current.DeviceId; device.Muted = false; Refuse();
            device.IgnoreWrites = true; try { output.Set(true); } catch (InvalidOperationException) { return; } throw new Exception("Unapplied mute reported success");
        });
        return failed;
    }

    sealed class FakeDevice : IOutputMuteDevice
    {
        public string Id { get; set; } = "fake-output";
        bool muted;
        public int Writes { get; private set; }
        public bool IgnoreWrites { get; set; }
        public bool Muted { get => muted; set { Writes++; if (!IgnoreWrites) muted = value; } }
        public void Dispose() { }
    }
}
