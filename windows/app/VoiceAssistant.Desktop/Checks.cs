using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public static class Checks
{
    public static async Task<int> RunAsync(string[] args)
    {
        var rows = new List<object>(); var failed = 0;
        if (args.Contains("--personal-data-only"))
        {
            failed = PersonalDataChecks.Run(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "personal-data-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, microphoneCapture = false, speakerPlayback = false, realAppExecution = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--english-commands-only"))
        {
            failed = EnglishCommandChecks.Run(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "english-command-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, microphoneCapture = false, speakerPlayback = false, realMutations = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--speech-capture-only"))
        {
            failed = SpeechCaptureStateChecks.Run(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "speech-capture-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, microphoneCapture = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--execution-consent-only"))
        {
            failed = await ExecutionConsentChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "execution-consent-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, visibleWindows = false, realMutations = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--multilingual-native"))
        {
            failed = await MultilingualNativeChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "multilingual-native-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, microphoneCapture = false, speakerPlayback = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--conversation-language-only"))
        {
            failed = await ConversationLanguageChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "conversation-language-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, microphoneCapture = false, speakerPlayback = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--input-device-native"))
        {
            failed = await InputDeviceChecks.RunNativeAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "input-device-native-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, microphoneCapture = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--codex-effort-native"))
        {
            failed = await CodexEffortChecks.RunNativeAsync(rows, args.Contains("--medium-only"), CancellationToken.None);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "codex-effort-native-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--mac-settings-only"))
        {
            failed = MacSettingsChecks.Run(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "mac-settings-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--browser-pages-only"))
        {
            failed = await BrowserPageChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "browser-page-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--text-undo-only"))
        {
            failed = await TextEditUndoChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "text-undo-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--whisper-prerequisites-only"))
        {
            failed = await WhisperPrerequisiteChecks.RunAsync(rows, args.Contains("--whisper-prerequisites-native"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-prerequisite-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--whisper-only"))
        {
            failed = await WhisperChecks.RunAsync(rows, args.Contains("--whisper-native"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--video-browser-only"))
        {
            failed = await BrowserVideoChecks.RunAsync(rows, args.Contains("--video-browser-native"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "video-browser-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--combo-only"))
        {
            failed = ComboChecks.Run(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "combo-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--phone-only"))
        {
            failed = await PhoneChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "phone-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--grow-only"))
        {
            failed = await GrowChecks.RunAsync(rows, args.Contains("--grow-service"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "grow-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--remote-only"))
        {
            failed = await RemoteChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "remote-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--pro-only"))
        {
            failed = await ProChecks.RunAsync(rows, args.Contains("--pro-service"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "pro-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--aside-only"))
        {
            failed = await AsideChecks.RunAsync(rows, args.Contains("--aside-native"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "aside-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--name-only"))
        {
            failed = NameLearningChecks.Run(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "name-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--barge-only"))
        {
            failed = await BargeInChecks.RunAsync(rows, args.Contains("--devices"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "barge-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--microphone-only"))
        {
            failed = await MicrophoneChecks.RunAsync(rows, args.Contains("--microphone-native"));
            failed += MicrophoneHealthChecks.Run(rows);
            failed += SpeechCaptureStateChecks.Run(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "microphone-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--kakao-only"))
        {
            failed = await KakaoChatChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "kakao-chat-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--display-only"))
        {
            failed = await DisplayPowerChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "display-power-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--sessions-only"))
        {
            failed = await ClaudeSessionChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "claude-session-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--api-tools-only"))
        {
            failed = await ApiToolChecks.RunAsync(rows);
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "api-tool-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--keys-only"))
        {
            failed = await KeyboardChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "keyboard-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--windows-only"))
        {
            failed = await WindowTargetChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "window-target-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--insert-only"))
        {
            failed = await InsertionChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "insertion-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--visual-only"))
        {
            failed = await VisualChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "visual-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--mcp-only"))
        {
            failed = await McpChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
            var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "mcp-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--controls-only"))
        {
            failed = await DesktopControlChecks.RunAsync(rows, args.Contains("--ai"));
            var controlsReport = Path.Combine(AppPaths.WindowsRoot, "artifacts", "desktop-control-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(controlsReport)!);
            File.WriteAllText(controlsReport, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--voice-only"))
        {
            failed = await VoiceChecks.RunAsync(rows, args.Contains("--devices"));
            var voiceReport = Path.Combine(AppPaths.WindowsRoot, "artifacts", "voice-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(voiceReport)!);
            File.WriteAllText(voiceReport, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--text-only"))
        {
            failed = await TextChecks.RunAsync(rows) + await DictationControlChecks.RunAsync(rows);
            var textReport = Path.Combine(AppPaths.WindowsRoot, "artifacts", "text-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(textReport)!);
            File.WriteAllText(textReport, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--browser-only"))
        {
            failed = await BrowserChecks.RunAsync(rows, args.Contains("--ai"));
            var browserReport = Path.Combine(AppPaths.WindowsRoot, "artifacts", "browser-checks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(browserReport)!);
            File.WriteAllText(browserReport, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
            return failed == 0 ? 0 : 1;
        }
        void Check(string name, Action test) { try { test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        void Equal<T>(T got, T want) { if (!Equals(got, want)) throw new Exception($"Expected {want}, got {got}"); }
        void Rejected(Action action) { try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } throw new Exception("Unexpectedly accepted"); }
        Check("WPF main and import review resources load", () =>
        {
            var main = new MainWindow(); var review = new SettingsImportWindow(new UiSettings { Name = "시험" }, new UiSettings());
            review.Close(); main.Close();
        });
        var cases = new[] { ("크롬 열어 줘", "open_app", "chrome"), ("메모장 켜", "open_app", "notepad"), ("계산기 실행해", "open_app", "calculator"), ("open notepad", "open_app", "notepad"), ("안녕하세요 라고 써 줘", "type", "안녕하세요"), ("입력해 테스트", "type", "테스트"), ("그만", "stop", ""), ("받아쓰기 시작", "dictation", ""), ("타이머 취소", "cancel_timers", ""), ("소리 키워", "volume_delta", ""), ("소리 줄여", "volume_delta", ""), ("다음 곡", "media", "next"), ("뒤로 가", "browser", "back"), ("새로고침", "browser", "refresh") };
        foreach (var c in cases) Check(c.Item1, () => { var p = CommandParser.Parse(c.Item1) ?? throw new Exception("unparsed"); Equal(p.Actions[0].Kind, c.Item2); Equal(p.Actions[0].Value, c.Item3); });
        Check("volume 30", () => Equal(CommandParser.Parse("볼륨 30으로 해줘")!.Actions[0].Number, 30));
        Check("timer 3 minutes", () => Equal(CommandParser.Parse("라면 3분")!.Actions[0].Number, 180));
        Check("Korean timer", () => Equal(CommandParser.Parse("삼 분 뒤에 알려 줘")!.Actions[0].Number, 180));
        Check("compound duration", () => Equal(CommandParser.Parse("1시간 30분 타이머")!.Actions[0].Number, 5400));
        Check("YouTube play", () => {var a=CommandParser.Parse("유튜브에서 로파이 음악 틀어")!.Actions[0];Equal(a.Kind,"youtube_play");Equal(a.Value,"로파이 음악");});
        Check("YouTube search only",()=>Equal(CommandParser.Parse("유튜브에서 재즈 검색해")!.Actions[0].Kind,"youtube_search"));
        Check("YouTube home requests never become video searches", () =>
        {
            foreach (var phrase in new[] { "유튜브열어줘.", "유튜브 열어 주세요", "유튜브 실행", "유튜브 실행해줘", "유튜브를 좀 켜 줘", "유튜브 홈페이지 열어 줘", "유튜브 창 보여줘", "유튜브", "YouTube 열어줘", "open YouTube", "open the YouTube home page" })
            { var action = CommandParser.Parse(phrase)!.Actions.Single(); Equal(action.Kind, "open_url"); Equal(action.Value, "https://www.youtube.com/"); }
        });
        Check("YouTube explicit titles that resemble commands stay searchable", () =>
        {
            foreach (var pair in new[] { ("유튜브에서 열어줘 검색해", "열어줘", "youtube_search"), ("유튜브 열어줘 틀어 줘", "열어줘", "youtube_play"), ("유튜브에서 재즈 검색해 주세요", "재즈", "youtube_search"), ("유튜브에서 로파이 재생해 주세요", "로파이", "youtube_play"), ("유튜브 재즈 영상 열어줘", "재즈 영상", "youtube_play") })
            { var action = CommandParser.Parse(pair.Item1)!.Actions.Single(); Equal(action.Kind, pair.Item3); Equal(action.Value, pair.Item2); }
        });
        Check("YouTube particles preserve artist and title syllables with omitted spaces", () =>
        {
            foreach (var pair in new[] { ("유튜브 에스파 틀어", "에스파"), ("유튜브에스파틀어줘", "에스파"), ("유튜브로파이틀어줘", "로파이"), ("유튜브에서로파이틀어줘", "로파이"), ("유튜브에 로파이 틀어줘", "로파이") })
                Equal(CommandParser.Parse(pair.Item1)!.Actions.Single().Value, pair.Item2);
            foreach (var phrase in new[] { "유튜브에서 검색해줘", "유튜브에서 틀어줘", "유튜브검색해주세요" })
            { var plan = CommandParser.Parse(phrase)!; Equal(plan.Actions.Count, 0); Equal(plan.Reply.Contains("어떤 영상"), true); }
            Equal(YouTubeCommands.OpensHome("유튜브 열어줘 검색해"), false);
        });
        Check("YouTube ordinal selection",()=>Equal(CommandParser.Parse("두 번째 영상 틀어")!.Actions[0].Number,2));
        Check("YouTube numeric selection",()=>Equal(CommandParser.Parse("10번째 거 재생해 줘")!.Actions[0].Number,10));
        Check("YouTube ordinal range",()=>Rejected(()=>ActionPolicy.Validate(CommandParser.Parse("99번째 영상 틀어")!.Actions[0])));
        Check("YouTube empty selection rejected",()=>Rejected(()=>new YouTubeSearch().Select(1)));
        Check("YouTube JSON escaped title and no ads",()=>{var html="var ytInitialData = {\"contents\":{\"twoColumnSearchResultsRenderer\":{\"primaryContents\":{\"items\":[{\"adSlotRenderer\":{\"videoRenderer\":{\"videoId\":\"AAAAAAAAAAA\",\"title\":{\"simpleText\":\"Ad\"}}}},{\"videoRenderer\":{\"videoId\":\"abcdefghijk\",\"title\":{\"runs\":[{\"text\":\"재즈 \\\"라이브\\\"\"}]}}},{\"videoRenderer\":{\"videoId\":\"abcdefghijk\",\"title\":{\"simpleText\":\"duplicate\"}}}]},\"secondaryContents\":{\"videoRenderer\":{\"videoId\":\"BBBBBBBBBBB\",\"title\":{\"simpleText\":\"Other\"}}}}}};</script>";var found=YouTubeSearch.ParseResults(html);Equal(found.Count,1);Equal(found[0].Title,"재즈 \"라이브\"");});
        Check("YouTube consent page has no fabricated videos",()=>Equal(YouTubeSearch.ParseResults("<html>Consent required</html>").Count,0));
        Check("YouTube player navigation boundary",()=>{Equal(VideoWindow.IsAllowedUrl("https://www.youtube.com/watch?v=abcdefghijk"),true);foreach(var url in new[]{"https://youtube.com.evil.test/watch", "https://user:pass@youtube.com", "file:///C:/Windows", "https://accounts.google.com"})Equal(VideoWindow.IsAllowedUrl(url),false);});
        Check("Video seek and range",()=>{Equal(CommandParser.Parse("30초 되감기")!.Actions[0].Number,-30);Rejected(()=>ActionPolicy.Validate(CommandParser.Parse("99분 빨리 감기")!.Actions[0]));});
        Check("Pause and resume are distinct",()=>{Equal(CommandParser.Parse("일시정지")!.Actions[0].Value,"pause");Equal(CommandParser.Parse("다시 재생")!.Actions[0].Value,"play");});
        Check("search escaped", () => Equal(CommandParser.Parse("C# & C++ 검색해")!.Actions[0].Value, "https://www.google.com/search?q=" + Uri.EscapeDataString("C# & C++")));
        Check("wake stripping", () => Equal(CommandParser.StripWake("자 비 스, 메모장 켜", "자비스"), "메모장 켜"));
        Check("no middle stripping", () => Equal(CommandParser.StripWake("왜 자비스야", "자비스"), "왜 자비스야"));
        Check("wake keeps Korean first syllable", () => Equal(CommandParser.StripWake("자비스 아빠 왔어", "자비스"), "아빠 왔어"));
        Check("unmatched goes to AI", () => Equal(CommandParser.Parse("광합성이 뭐야"), null));
        Check("shell action refused", () => Rejected(() => ActionPolicy.Validate(new("shell", "calc.exe"))));
        foreach (var url in new[] { "file:///C:/Windows/System32/cmd.exe", "javascript:alert(1)", "powershell:abc", "https://user:password@example.com" }) Check("deny URL " + url, () => Rejected(() => ActionPolicy.Validate(new("open_url", url))));
        Check("arbitrary app refused", () => Rejected(() => ActionPolicy.Validate(new("open_app", "powershell.exe -Command whoami"))));
        Check("unbounded timer refused", () => Rejected(() => ActionPolicy.Validate(new("timer", Number: 86401))));
        Check("arbitrary hotkey refused", () => Rejected(() => ActionPolicy.Validate(new("key", "win+r"))));
        Check("AI type requires review", () => Equal(ActionPolicy.NeedsConfirmation(new("type", "hello"), true), true));
        Check("enter requires review", () => Equal(ActionPolicy.NeedsConfirmation(new("key", "enter"), false), true));
        Check("tap requires review", () => Equal(ActionPolicy.NeedsConfirmation(new("tap", "보내기"), true), true));
        Check("terminal protected", () => Equal(WindowsActions.IsProtectedProcess("powershell"), true));
        Check("password manager protected", () => Equal(WindowsActions.IsProtectedProcess("1Password"), true));
        Check("notepad permitted", () => Equal(WindowsActions.IsProtectedProcess("notepad"), false));
        Check("remote Whisper refused", () => Rejected(() => SettingsStore.ValidateLocalUrl("https://example.com")));
        Check("NaN silence refused", () => Rejected(() => SettingsStore.Validate(new UiSettings { SilenceSeconds = double.NaN })));
        Check("settings roundtrip", () => { var s = new UiSettings { Name = "자비스", SilenceSeconds = 0.8 }; var json = JsonSerializer.Serialize(s); var again = JsonSerializer.Deserialize<UiSettings>(json)!; Equal(again.Name, s.Name); Equal(again.SilenceSeconds, s.SilenceSeconds); });
        Check("hold activation settings",()=>SettingsStore.Validate(new UiSettings{Activation="hold"}));
        Check("WebView2 runtime available",()=>{if(string.IsNullOrWhiteSpace(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()))throw new Exception("WebView2 Runtime missing");});
        Check("Speech options range",()=>{Rejected(()=>SettingsStore.Validate(new UiSettings{SpeechRate=6}));Rejected(()=>SettingsStore.Validate(new UiSettings{SpeechVolume=101}));});
        Check("Installed speech voices and outputs",()=>{if(SpeechOutput.Options(false).Count<2||SpeechOutput.Options(true).Count<2)throw new Exception("No installed voice or output");});
        Check("Speech preferences roundtrip",()=>{var original=new UiSettings{VoiceId="voice-fixture",OutputId="speaker-fixture",SpeechRate=-2,SpeechVolume=65};var copy=JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(original))!;Equal(copy.VoiceId,original.VoiceId);Equal(copy.OutputId,original.OutputId);Equal(copy.SpeechRate,-2);Equal(copy.SpeechVolume,65);});
        try
        {
            using var speech=new SpeechOutput();bool missingRejected=false;
            try{await speech.SpeakAsync("This must not play.",CancellationToken.None,new UiSettings{VoiceId="missing-test-voice"});}
            catch(InvalidOperationException ex)when(ex.Message.Contains("선택한 목소리가 없어요")){missingRejected=true;}
            if(!missingRejected)throw new Exception("Missing selected voice was silently replaced");
            rows.Add(new{name="Missing chosen voice reported without fallback",pass=true});
        }
        catch(Exception ex){failed++;rows.Add(new{name="Missing chosen voice reported without fallback",pass=false,error=ex.Message});}
        var stateDir = Path.Combine(AppPaths.WindowsRoot, "artifacts", "state-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stateDir); var stateFile = Path.Combine(stateDir, "personal.json"); var personal = new Personalization(stateFile);
        Check("routine saves and reopens", () => { var p = personal.Resolve("앞으로 '작업 시작' 하면 메모장 열고 소리 30으로 해")!; personal.Execute(p.Actions[0]); var loaded = new Personalization(stateFile).Resolve("작업 시작")!; Equal(loaded.Actions.Count, 2); Equal(loaded.Actions[0].Kind, "open_app"); Equal(loaded.Actions[1].Number, 30); });
        Check("unsafe routine refused", () => Rejected(() => personal.Resolve("앞으로 '인사' 하면 안녕하세요 라고 써 줘")));
        Check("memory persisted", () => { personal.Execute(new("remember", "차는 녹차를 좋아한다")); Equal(new Personalization(stateFile).MemoryContext, "차는 녹차를 좋아한다"); });
        Check("typing never learned", () => Equal(Personalization.CanReuse(new[] { new AssistantAction("type", "hello") }), false));
        Check("multi command", () => Equal(personal.Resolve("메모장 열고 소리 줄여")!.Actions.Count, 2));
        Check("delegate requires review", () => Equal(ActionPolicy.NeedsConfirmation(new("delegate", "버그 고쳐"), false), true));
        Check("voice delegate parsed", () => Equal(personal.Resolve("클로드한테 로그인 버그 고쳐 줘")!.Actions[0].Kind, "delegate"));
        Check("timers survive restart and fire once", () => { var path = Path.Combine(stateDir, "timers.json"); var saved = new TimerStore(path); saved.Add(30, "테스트"); var reopened = new TimerStore(path); Equal(reopened.Items.Count, 1); Equal(reopened.TakeDue(DateTimeOffset.Now.AddMinutes(1)).Count, 1); Equal(new TimerStore(path).Items.Count, 0); });
        Check("cancelled timer stays cancelled", () => { var path = Path.Combine(stateDir, "cancelled.json"); var saved = new TimerStore(path); saved.Add(30, "테스트"); saved.Clear(); Equal(new TimerStore(path).Items.Count, 0); });
        Check("timer list", () => Equal(CommandParser.Parse("타이머 목록")!.Actions[0].Kind, "list_timers"));
        Check("selected text rewrite", () => Equal(CommandParser.Parse("선택한 글 공손하게 다듬어 줘")!.Actions[0].Kind, "rewrite"));
        Check("Codex provider settings",()=>SettingsStore.Validate(new UiSettings{BrainProvider="codex"}));
        Check("Codex delegation",()=>Equal(personal.Resolve("코덱스한테 테스트 고쳐 줘")!.Actions[0].Kind,"delegate_codex"));
        Check("Codex delegation reviewed",()=>Equal(ActionPolicy.NeedsConfirmation(new("delegate_codex","test"),false),true));
        Check("Codex planner stays read-only",()=>{var p=Brain.CodexInfo("codex.exe","schema.json",new UiSettings());Equal(p.CreateNoWindow,true);Equal(p.ArgumentList.Contains("read-only"),true);Equal(p.ArgumentList.Contains("--ignore-user-config"),true);Equal(p.ArgumentList.Any(x=>x.Contains("dangerously")),false);Equal(p.ArgumentList.Last(),"-");});
        Check("Codex waits for completed response",()=>Rejected(()=>Brain.DecodeCodexEvents("{\"type\":\"turn.started\"}")));
        Check("Codex JSON event response",()=>{var body=JsonSerializer.Serialize(new{reply="완료",actions=Array.Empty<AssistantAction>()});var events=JsonSerializer.Serialize(new{type="item.completed",item=new{type="agent_message",text=body}})+"\n{\"type\":\"turn.completed\"}";Equal(Brain.DecodeCodexEvents(events).Reply,"완료");});
        failed += await ApiChecks.RunAsync(rows);
        failed += await AsideChecks.RunAsync(rows);
        failed += ComboChecks.Run(rows);
        failed += await BrowserVideoChecks.RunAsync(rows);
        failed += await ProChecks.RunAsync(rows);
        failed += await RemoteChecks.RunAsync(rows);
        failed += await PhoneChecks.RunAsync(rows);
        failed += await GrowChecks.RunAsync(rows);
        failed += await ApiToolChecks.RunAsync(rows);
        failed += await KakaoChatChecks.RunAsync(rows);
        failed += await ClaudeSessionChecks.RunAsync(rows);
        failed += await DisplayPowerChecks.RunAsync(rows);
        failed += await MicrophoneChecks.RunAsync(rows, false);
        failed += MicrophoneHealthChecks.Run(rows);
        failed += SpeechCaptureStateChecks.Run(rows);
        failed += NameLearningChecks.Run(rows);
        failed += EnglishCommandChecks.Run(rows);
        failed += PersonalDataChecks.Run(rows);
        failed += ListeningStateChecks.Run(rows);
        failed += VocabularyChecks.Run(rows);
        failed += await BargeInChecks.RunAsync(rows, args.Contains("--devices"));
        failed += InstanceScopeChecks.Run(rows);
        failed += await ActionChecks.RunAsync(rows);
        failed += DesktopChecks.Run(rows);
        failed += await TextChecks.RunAsync(rows);
        failed += await DictationControlChecks.RunAsync(rows);
        failed += await VoiceChecks.RunAsync(rows, args.Contains("--devices"));
        failed += await DesktopControlChecks.RunAsync(rows, args.Contains("--ai"));
        failed += await McpChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
        failed += await VisualChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
        failed += await InsertionChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
        failed += await BrowserPageChecks.RunAsync(rows);
        failed += await TextEditUndoChecks.RunAsync(rows);
        failed += await WindowTargetChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
        failed += await KeyboardChecks.RunAsync(rows, args.Contains("--ai"), args.Contains("--codex"));
        failed += SettingsTransferChecks.Run(rows);
        failed += MacSettingsChecks.Run(rows);
        failed += WakePhraseChecks.Run(rows);
        failed += CodexEffortChecks.Run(rows);
        failed += InputDeviceChecks.Run(rows);
        failed += await ConversationLanguageChecks.RunAsync(rows);
        failed += await ExecutionConsentChecks.RunAsync(rows);
        failed += await CodexEffortChecks.RunProcessAsync(rows);
        failed += await DistributionChecks.RunAsync(rows);
        failed += await WhisperChecks.RunAsync(rows);
        failed += WhisperWakeChecks.Run(rows);
        failed += await WhisperPrerequisiteChecks.RunAsync(rows);
        failed += await AgentChecks.RunAsync(rows, args.Contains("--ai"));
        failed += await BrowserChecks.RunAsync(rows, args.Contains("--ai"));
        if(args.Contains("--codex"))
        {
            try{var p=await new Brain().AskAsync("한국어로 '연결 성공'이라고만 답해.","",new UiSettings{BrainProvider="codex"},CancellationToken.None);if(!p.Reply.Contains("연결 성공")||p.Actions.Count!=0)throw new Exception(p.Reply);rows.Add(new{name="Codex actual connection",pass=true,reply=p.Reply});}catch(Exception ex){failed++;rows.Add(new{name="Codex actual connection",pass=false,error=ex.Message});}
        }
        if(args.Contains("--youtube"))
        {
            try{var service=new YouTubeSearch();var videos=await service.SearchAsync("Blender Big Buck Bunny official",CancellationToken.None);if(videos.Count<2)throw new Exception("Expected at least 2 real results");Equal(service.Select(2).Id,videos[1].Id);rows.Add(new{name="YouTube actual search and second selection",pass=true,videos});}catch(Exception ex){failed++;rows.Add(new{name="YouTube actual search and second selection",pass=false,error=ex.Message});}
        }
        if (args.Contains("--ai"))
        {
            try { var reply = await new Brain().AskAsync("한국어로 '연결 성공'이라고만 답해.", "", new UiSettings(), CancellationToken.None); if (!reply.Reply.Contains("연결 성공") || reply.Actions.Count != 0) throw new Exception(reply.Reply); rows.Add(new { name = "Claude actual connection", pass = true, reply = reply.Reply }); }
            catch (Exception ex) { failed++; rows.Add(new { name = "Claude actual connection", pass = false, error = ex.Message }); }
        }
        if (args.Contains("--devices"))
        {
            try { var devices = await SpeechInput.DevicesAsync(); if (devices.Count < 2) throw new Exception("No input device"); rows.Add(new { name = "actual microphone enumeration", pass = true, count = devices.Count }); }
            catch (Exception ex) { failed++; rows.Add(new { name = "actual microphone enumeration", pass = false, error = ex.Message }); }
        }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
