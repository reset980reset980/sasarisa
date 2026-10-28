using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

internal static class EnglishCommands
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    static CommandPlan One(string reply, string kind, string value = "", int number = 0) => new(reply, new[] { new AssistantAction(kind, value, number) });
    static CommandPlan Notice(string text) => new(text, Array.Empty<AssistantAction>());
    static string Unquote(string value) => value.Length >= 2 && (value[0], value[^1]) is ('"', '"') or ('“', '”') or ('\'', '\'') ? value[1..^1] : value;
    static bool Quoted(string value) => Unquote(value) != value;
    static string OutsideQuotes(string value) => Regex.Replace(value, "[\"“][^\"”]*[\"”]", " ");
    internal static bool RequiresPlanning(string text) => Regex.IsMatch(OutsideQuotes(text),
        @"\b(?:if|unless|when|then|depending on|provided that|after\s+(?:checking|reading|finding|searching))\b|\b(?:and|also)\s+(?:send|delete|open|close|click|press|pay|buy|play|pause|search|find|set|turn|mute|unmute|type|write|start|stop|resume|cancel|list|show|translate|make|undo|rewind|refresh|reload|switch|select|skip)\b", Options);
    static bool Canceled(string text) => Regex.IsMatch(text, @"(?:[,;]\s*|\s+)(?:never\s*mind|cancel that|scratch that)[.!?]*$", Options);
    static string Sentence(string raw) => Regex.Replace(raw.Trim().TrimEnd('.', '!', '?'), @"\s+", " ").Replace('’', '\'');

    internal static CommandPlan? Parse(string input, AppRegistry registry, WebSearchSession? searches)
    {
        var raw = input.Trim();
        var typed = Regex.Match(raw, @"^(?:please\s+)?(?:type|write)\s+([\s\S]+)$", Options);
        if (typed.Success)
        {
            var payload = typed.Groups[1].Value;
            if (Quoted(payload)) return One("Typed the requested text.", "type", Unquote(payload));
            if (Canceled(raw)) return One("Stopped.", "stop");
            return RequiresPlanning(raw) ? null : One("Typed the requested text.", "type", payload);
        }
        if (Canceled(raw)) return One("Stopped.", "stop");
        var text = Sentence(raw);
        text = Regex.Replace(text, @"^please\s+", "", Options);
        text = Regex.Replace(text, @"(?:,\s*|\s+)please$", "", Options);
        // Reminder labels are data, including words such as "if" or "delete".
        var reminder = Regex.Match(text, @"^remind me in (.+?)\s+to\s+(.+)$", Options);
        if (reminder.Success && !RequiresPlanning(reminder.Groups[1].Value) && Duration(reminder.Groups[1].Value, out var reminderSeconds))
            return Timer(reminderSeconds, Unquote(reminder.Groups[2].Value));
        // A new assistant handoff carries the entire task as data. "Tell Claude"
        // still requires observing and selecting an existing session.
        var handoff = Regex.Match(raw, @"^(?:please\s+)?(?:ask|have)\s+(claude(?:\s+code)?|codex)\s+(?:to\s+)?([\s\S]+)$", Options);
        if (handoff.Success) return One("Opened the coding assistant. Check its progress and permission requests in that window.", handoff.Groups[1].Value.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "delegate_codex" : "delegate", handoff.Groups[2].Value);
        if (RequiresPlanning(text)) return null;
        var phrase = text.ToLowerInvariant();
        switch (phrase)
        {
            case "stop": case "quiet": case "shut up": case "hush": case "wait": case "hold on": case "okay stop": case "ok stop":
            case "that's all": case "finish": case "thanks": case "thank you": case "good night": case "never mind": case "nevermind": case "cancel that": case "scratch that":
            case "go to sleep": case "stop listening": case "stop dictation": case "end dictation": case "finish dictation": return One("Stopped.", "stop");
            case "enter": case "press enter": case "send": return One("Requested Enter.", "key", "enter");
            case "undo": case "undo that": return One("", "undo");
            case "what did you just do": case "what did you do": case "action history": return One("", "history");
            case "close what you opened": case "close the window you opened": return One("", "close_owned");
            case "what time is it": case "what's the time": case "what is the time": case "tell me the time": case "time":
                return One("It's " + DateTime.Now.ToString("h:mm tt", English) + ".", "answer");
            case "what day is it": case "what's the date": case "what is the date": case "what's today's date": case "today's date": case "date":
                return One("Today is " + DateTime.Now.ToString("dddd, MMMM d, yyyy", English) + ".", "answer");
            case "take dictation": case "dictation mode": case "start dictation": return One("Dictation started. Select the field and speak; say 'stop dictation' to finish.", "dictation");
            case "cancel timers": case "cancel all timers": case "clear all timers": return One("Canceled all timers.", "cancel_timers");
            case "list timers": case "show timers": case "show my timers": case "what timers are running": case "timer list": return One("", "list_timers");
            case "mute": case "mute sound": case "mute audio": case "mute the volume": return One("Muted the output.", "media", "mute_on");
            case "unmute": case "unmute sound": case "unmute audio": case "unmute the volume": return One("Unmuted the output.", "media", "unmute");
            case "go back": case "back": return One("Went back in the browser.", "browser", "back");
            case "go forward": case "forward": return One("Went forward in the browser.", "browser", "forward");
            case "refresh": case "reload": return One("Refreshed the browser page.", "browser", "refresh");
            case "pause music": case "pause video": case "pause the music": case "pause the video": case "stop music": case "stop video": case "pause youtube": case "youtube pause": return One("Requested pause.", "media", "pause");
            case "resume": case "play again": case "resume music": case "resume video": case "resume youtube": return One("Requested playback.", "media", "play");
            case "next video": case "next song": case "next track": case "skip": return One("Requested the next item.", "media", "next");
            case "turn off the screen": case "turn off screen": case "screen off": case "turn off the display": case "turn off the monitor": case "display off": case "monitor off": return One("Requested display off.", "display_off");
        }
        var timer = Regex.Match(text, @"^(?:set\s+)?(?:a\s+)?timer(?:\s+for)?\s+(.+)$", Options);
        if (!timer.Success) timer = Regex.Match(text, @"^(?:set\s+(?:a\s+)?)?(.+?)\s*timer$", Options);
        if (!timer.Success) timer = Regex.Match(text, @"^remind me in (.+)$", Options);
        if (timer.Success && Duration(timer.Groups[1].Value, out var seconds)) return Timer(seconds, "Timer");
        var volume = Volume(text); if (volume != null) return volume;
        var seek = Regex.Match(text, @"^(fast\s*forward|skip ahead|rewind)(?:\s+(\d{1,5})\s*(seconds?|minutes?))?$", Options);
        if (seek.Success)
        {
            var amount = seek.Groups[2].Success ? int.Parse(seek.Groups[2].Value, CultureInfo.InvariantCulture) * (seek.Groups[3].Value.StartsWith("min", StringComparison.OrdinalIgnoreCase) ? 60 : 1) : 10;
            if (amount is < 1 or > 3600) return Notice("Choose a seek duration between 1 second and 1 hour.");
            return One("Requested a playback position change.", "seek", number: seek.Groups[1].Value.Equals("rewind", StringComparison.OrdinalIgnoreCase) ? -amount : amount);
        }
        if (Regex.IsMatch(text, @"^(?:make this (?:more polite|polite|formal|casual|shorter|longer)|shorten this|translate (?:this|the selected text) (?:to|into) (?:english|korean)|fix (?:the )?(?:spelling|grammar)|polish (?:this|the selected text))$", Options))
            return One("Updated the selected text.", "rewrite", text);
        var app = Regex.Match(text, @"^open\s+(.+)$", Options);
        if (app.Success)
        {
            var name = app.Groups[1].Value;
            if (CommandParser.Apps.TryGetValue(name, out var target)) return One("Opened " + name + ".", "open_app", target);
            if (registry.Resolve(name) is { } registered) return One("Opened " + registered.Name + ".", "open_app", registered.Id);
            // These are fallbacks, not newly reserved registry names. Existing
            // user aliases must keep working and remain editable after updates.
            var fallback = name.ToLowerInvariant() switch { "notes" or "textedit" => "notepad", "finder" or "file explorer" => "explorer", _ => null };
            if (fallback != null) return One("Opened " + name + ".", "open_app", fallback);
        }
        if (YouTubeCommands.OpensHome(text)) return One("Opened YouTube.", "open_url", "https://www.youtube.com/");
        var play = Regex.Match(text, @"^play\s+(.+?)\s+on\s+youtube$", Options);
        if (play.Success) return One("Requested the first video.", "youtube_play", Unquote(play.Groups[1].Value));
        var nth = Regex.Match(text, @"^(?:play\s+)?(?:the\s+)?(first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|[1-9]|10)(?:st|nd|rd|th)?\s+(?:video|result)$", Options);
        if (nth.Success)
        {
            var ordinals = new[] { "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth" };
            var number = int.TryParse(nth.Groups[1].Value, out var ordinal) ? ordinal : Array.IndexOf(ordinals, nth.Groups[1].Value.ToLowerInvariant()) + 1;
            return One("Requested the selected video.", "youtube_select", number: number);
        }
        var tube = Regex.Match(text, @"^youtube\s+(?:(search(?:\s+for)?|play)\s+)?(.+)$", Options);
        if (tube.Success && !Regex.IsMatch(tube.Groups[2].Value, "[가-힣]")) return One("Requested YouTube results.", tube.Groups[1].Value.StartsWith("search", StringComparison.OrdinalIgnoreCase) ? "youtube_search" : "youtube_play", Unquote(tube.Groups[2].Value));
        return Search(text, searches);
    }

    static CommandPlan Timer(decimal seconds, string label) => seconds is < 1 or > 86400 || decimal.Truncate(seconds) != seconds
        ? Notice("Choose a timer duration between 1 second and 24 hours.")
        : One("Started a timer for " + seconds.ToString(CultureInfo.InvariantCulture) + " seconds.", "timer", label, (int)seconds);
    static readonly string[] Small = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    static bool Quantity(string text, out decimal number)
    {
        text = text.ToLowerInvariant();
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)) return true;
        if (text is "a" or "an") { number = 1; return true; }
        if (text == "half") { number = .5m; return true; }
        var parts = text.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var small = Array.IndexOf(Small, text); if (small >= 0) { number = small; return true; }
        var tens = Array.IndexOf(new[] { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" }, parts[0]);
        if (tens < 2 || parts.Length > 2) return false;
        var rest = parts.Length == 1 ? 0 : Array.IndexOf(Small, parts[1]); if (rest is < 0 or > 9) return false;
        number = tens * 10 + rest; return true;
    }
    static bool Duration(string text, out decimal seconds)
    {
        seconds = 0;
        var tokens = Regex.Matches(text, @"(?<![\p{L}\p{N}])(?<number>\d{1,6}(?:\.\d{1,3})?|half|an?|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[- ](?:one|two|three|four|five|six|seven|eight|nine))?)\s*-?\s*(?<unit>hours?|hrs?|minutes?|mins?|seconds?|secs?)\b", Options);
        int end = 0;
        foreach (Match token in tokens)
        {
            if (!Regex.IsMatch(text[end..token.Index], @"^\s*(?:(?:,|and)\s*)?$", Options) || !Quantity(token.Groups["number"].Value, out var number)) return false;
            var unit = token.Groups["unit"].Value.ToLowerInvariant(); seconds += number * (unit.StartsWith('h') ? 3600 : unit.StartsWith('m') ? 60 : 1); end = token.Index + token.Length;
        }
        return tokens.Count > 0 && string.IsNullOrWhiteSpace(text[end..]);
    }
    static CommandPlan? Volume(string text)
    {
        var value = Regex.Match(text, @"^(?:set\s+)?(?:the\s+)?(?:volume|sound)(?:\s+(?:to|at))?\s+(\d{1,3})(?:\s*(?:percent|%))?$", Options);
        if (value.Success) { var number = int.Parse(value.Groups[1].Value); return number > 100 ? Notice("Choose a volume between 0 and 100 percent.") : One("Set volume to " + number + " percent.", "volume", number: number); }
        if (Regex.IsMatch(text, @"^(?:(?:set|make)\s+)?(?:the\s+)?(?:volume|sound)\s+(?:comfortable|to a comfortable level)$", Options)) return One("Set volume to 50 percent.", "volume", number: 50);
        var delta = Regex.Match(text, @"^(?:(?:turn|make)\s+)?(?:the\s+)?(?:volume|sound)\s+(up|down|higher|lower|louder|quieter)(?:\s+(a lot|much|a little|a bit))?$", Options);
        if (!delta.Success) delta = Regex.Match(text, @"^(?:turn\s+)?(up|down|louder|quieter)\s+(?:the\s+)?(?:volume|sound)(?:\s+(a lot|much|a little|a bit))?$", Options);
        if (!delta.Success) return null;
        var amount = delta.Groups[2].Value.ToLowerInvariant(); var step = amount is "a lot" or "much" ? 30 : amount is "a little" or "a bit" ? 7 : 15;
        var direction = delta.Groups[1].Value.ToLowerInvariant(); if (direction is "down" or "lower" or "quieter") step = -step;
        return One("Adjusted output volume.", "volume_delta", number: step);
    }
    static CommandPlan? Search(string text, WebSearchSession? searches)
    {
        const string sites = "google|amazon|coupang|naver shopping|naver";
        var siteQuery = Regex.Match(text, "^search (" + sites + @") for (.+)$", Options);
        if (!siteQuery.Success)
        {
            var trailing = Regex.Match(text, @"^(?:search(?: for)?|shop for|buy|find) (.+) on (" + sites + ")$", Options);
            if (trailing.Success) siteQuery = Regex.Match(trailing.Groups[2].Value + " for " + trailing.Groups[1].Value, "^(" + sites + @") for (.+)$", Options);
        }
        if (siteQuery.Success)
        {
            var id = siteQuery.Groups[1].Value.Equals("naver shopping", StringComparison.OrdinalIgnoreCase) ? "shopping" : siteQuery.Groups[1].Value.ToLowerInvariant();
            return SearchUrl(WebCommands.Sites.Single(x => x.Id == id), siteQuery.Groups[2].Value);
        }
        var generic = Regex.Match(text, @"^(?:search(?: for)?|find|look up|google)\s+(.+)$", Options);
        return generic.Success ? SearchUrl(text.StartsWith("google ", StringComparison.OrdinalIgnoreCase) ? WebCommands.Sites[0] : searches?.Recent ?? WebCommands.Sites[0], generic.Groups[1].Value) : null;
    }
    static CommandPlan SearchUrl(WebSite site, string query)
    {
        query = Unquote(query);
        return query.Length is < 1 or > 500 ? Notice("Use a search query of 1 to 500 characters.") : One("Opened " + site.Id + " search results.", "open_url", site.Search + Uri.EscapeDataString(query));
    }
}
