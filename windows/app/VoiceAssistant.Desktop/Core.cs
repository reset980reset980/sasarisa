using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

public static class AppPaths
{
    public static readonly string StandardHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistantWindows");
    public static readonly string Home = Environment.GetEnvironmentVariable("VOICE_ASSISTANT_HOME") ?? StandardHome;
    public static readonly string InstancePrefix = InstanceScope.ForHome(Home, StandardHome);
    public static string WindowsRoot { get; private set; } = "";
    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string Python => File.Exists(Path.Combine(WindowsRoot, "python", "python.exe")) ? Path.Combine(WindowsRoot, "python", "python.exe") : Path.Combine(WindowsRoot, "poc", ".venv", "Scripts", "python.exe");
    public static string Worker => Path.Combine(WindowsRoot, "runtime", "speech_worker.py");
    public static string VadModel => File.Exists(Path.Combine(WindowsRoot, "runtime", "assets", "silero_vad.onnx")) ? Path.Combine(WindowsRoot, "runtime", "assets", "silero_vad.onnx") : Path.Combine(WindowsRoot, "artifacts", "package-cache", "silero_vad.onnx");
    public const string ModelName = "sherpa-onnx-streaming-zipformer-korean-2024-06-16";
    public static string UserModel => Path.Combine(Home, "models", ModelName);
    public static string Model => SetupReadiness.HasModel(UserModel) ? UserModel : Directory.Exists(Path.Combine(WindowsRoot, "poc", "models", ModelName)) ? Path.Combine(WindowsRoot, "poc", "models", ModelName) : UserModel;
    public static void Initialize()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Path.Combine(Home, "work"));
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "runtime", "speech_worker.py"))) { WindowsRoot = d.FullName; return; }
                if (File.Exists(Path.Combine(d.FullName, "windows", "runtime", "speech_worker.py"))) { WindowsRoot = Path.Combine(d.FullName, "windows"); return; }
            }
    }
    public static void Log(string kind, string text)
    {
        try { lock (typeof(AppPaths)) { var path = Path.Combine(Home, "assistant.log"); if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".previous", true); File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{kind}] {text.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}"); } } catch { }
    }
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static UiSettings Load()
    {
        if (!File.Exists(AppPaths.ConfigFile)) return new UiSettings();
        try { var s = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(AppPaths.ConfigFile), Json) ?? new(); Validate(s); return s; }
        catch (Exception ex) { AppPaths.Log("settings", "설정을 읽지 못해 기본값 사용: " + ex.Message); return new(); }
    }
    public static void Validate(UiSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > 20) throw new ArgumentException("호출 이름은 1~20자로 입력하세요.");
        s.LearnedWakes ??= new(); LearnedNames.Validate(s.LearnedWakes);
        WakePhrases.Validate(s.WakeWords);
        InputDeviceSelection.Validate(s.Device, s.DeviceLabel);
        _ = ConversationLanguage.Get(s.Language);
        _ = ExecutionConsent.ModeLabel(s.ConfirmDelegate);
        _ = CodexEffort.Label(s.BrainEffort);
        if (s.Activation is not ("voice" or "hotkey" or "hold")) throw new ArgumentException("호출 방식을 확인하세요.");
        if (s.BargeIn is not ("auto" or "any" or "wake" or "off")) throw new ArgumentException("답변 중 끼어들기 방식을 확인하세요.");
        if (s.GrowTogether is not ("off" or "solo" or "together")) throw new ArgumentException("공유 학습 방식을 확인하세요.");
        if (s.YouTubePlayer is not ("embedded" or "chrome" or "edge")) throw new ArgumentException("유튜브 재생 방식을 확인하세요.");
        if(s.SpeechRate< -5||s.SpeechRate>5||s.SpeechVolume<0||s.SpeechVolume>100)throw new ArgumentException("답변 속도는 -5~5, 음량은 0~100으로 입력하세요.");
        if (s.SilenceSeconds < 0.4 || s.SilenceSeconds > 3 || !double.IsFinite(s.SilenceSeconds)) throw new ArgumentException("말 끝 대기는 0.4~3초입니다.");
        if (s.FollowUpSeconds < 5 || s.FollowUpSeconds > 120 || !double.IsFinite(s.FollowUpSeconds)) throw new ArgumentException("대화 유지 시간은 5~120초입니다.");
        if (s.Threads < 1 || s.Threads > 8) throw new ArgumentException("처리 스레드는 1~8개입니다.");
        if (s.KeywordThreshold < 0.01 || s.KeywordThreshold > 0.9 || !double.IsFinite(s.KeywordThreshold)) throw new ArgumentException("이름 감지 임계값은 0.01~0.9입니다.");
        if (!double.IsFinite(s.InputGain) || s.InputGain < .25 || s.InputGain > 20) throw new ArgumentException("인식 입력 증폭은 0.25~20배입니다.");
        if (s.BrainProvider is not ("claude" or "codex" or "ollama" or "aside" or "combo") && ApiProviders.Find(s.BrainProvider) == null) throw new ArgumentException("AI 종류를 확인하세요.");
        if (s.BrainProvider == "ollama") ValidateLocalUrl(s.BrainUrl);
        if (ApiProviders.Find(s.BrainProvider) != null) ApiProviders.Endpoint(s.BrainProvider, s.BrainUrl);
        if (s.ProviderProfiles == null) s.ProviderProfiles = new();
        s.Desktop ??= new(); s.Desktop.Validate(s.Activation);
        s.Text ??= new(); s.Text.Validate();
        s.Tts ??= new(); s.Tts.Validate();
        if (!string.IsNullOrWhiteSpace(s.WhisperUrl)) ValidateLocalUrl(s.WhisperUrl);
        if (s.WhisperMode is not ("off" or "external" or "cpu" or "cuda")) throw new ArgumentException("음성 보정 방식을 확인하세요.");
    }
    public static Uri ValidateLocalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || !u.IsLoopback || !string.IsNullOrEmpty(u.UserInfo)) throw new ArgumentException("로컬 서버 주소는 http://127.0.0.1:포트 형식으로 입력하세요.");
        return u;
    }
    public static void Save(UiSettings s) => SaveWithBackup(s, ".backup");
    public static void SaveVocabulary(UiSettings s, bool addedWords) => SaveWithBackup(s, addedWords ? ".vocabulary-backup" : null);
    static void SaveWithBackup(UiSettings s, string? backupSuffix)
    {
        Validate(s);
        var temp = AppPaths.ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(s, Json));
            if (File.Exists(AppPaths.ConfigFile)) File.Replace(temp, AppPaths.ConfigFile, backupSuffix == null ? null : AppPaths.ConfigFile + backupSuffix);
            else File.Move(temp, AppPaths.ConfigFile);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record AssistantAction(string Kind, string Value = "", int Number = 0, string Target = "", int X = 0, int Y = 0);
public sealed record CommandPlan(string Reply, IReadOnlyList<AssistantAction> Actions);

public static class CommandParser
{
    public static readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["메모장"] = "notepad",
        ["메모"] = "notepad",
        ["notepad"] = "notepad",
        ["notes"] = "notepad",
        ["textedit"] = "notepad",
        ["계산기"] = "calculator",
        ["calculator"] = "calculator",
        ["크롬"] = "chrome",
        ["구글 크롬"] = "chrome",
        ["chrome"] = "chrome",
        ["엣지"] = "edge",
        ["edge"] = "edge",
        ["탐색기"] = "explorer",
        ["파일 탐색기"] = "explorer",
        ["explorer"] = "explorer",
        ["finder"] = "explorer",
        ["file explorer"] = "explorer",
        ["카톡"] = "kakao",
        ["카카오톡"] = "kakao",
        ["클로드"] = "claude",
        ["claude"] = "claude",
        ["브이에스코드"] = "vscode",
        ["비주얼 스튜디오 코드"] = "vscode",
        ["코드"] = "vscode"
    };
    public static string Normalize(string s) => Regex.Replace(s.Trim(), @"\s+", " ").TrimEnd('.', '!', '?', '。');
    public static bool RequiresObservations(string text) => EnglishCommands.RequiresPlanning(text) || Regex.IsMatch(text, @"검색해서|찾아서|확인해서|살펴보고|확인한\s*(?:뒤|후)|결과\s*중|목록\s*중|제목에.*(?:포함|있는)|있으면|없으면|경우에는");
    public static string StripWake(string text, string name)
    {
        var pattern = string.Join(@"\s*", name.Trim().Select(c => Regex.Escape(c.ToString())));
        return Regex.Replace(text.Trim(), @"^(?:(?:헤이|하이|hey)\s*)?" + pattern + @"(?:[야아](?=\s|[~,.!]|$))?[~,.!\s]*", "", RegexOptions.IgnoreCase).Trim();
    }
    static CommandPlan One(string reply, string kind, string value = "", int number = 0) => new(reply, new[] { new AssistantAction(kind, value, number) });
    public static CommandPlan? Parse(string input) => Parse(input, AppRegistry.Default);
    public static CommandPlan? Parse(string input, WebSearchSession searches) => Parse(input, AppRegistry.Default, searches);
    public static CommandPlan? Parse(string input, AppRegistry registry, WebSearchSession? searches = null)
    {
        if (EnglishCommands.Parse(input, registry, searches) is { } english) return english;
        var s = Normalize(input); var compact = s.Replace(" ", "");
        if (compact is "그만" or "됐어" or "멈춰" or "중지" or "취소" or "stop") return One("멈췄어요.", "stop");
        if (Regex.IsMatch(compact, @"^(?:화면|모니터|디스플레이)(?:을|를)?(?:꺼(?:줘|주세요)?|끄기|끄세요|끄자)$") ||
            Regex.IsMatch(s, @"^(?:turn off (?:the )?(?:screen|display|monitor)|(?:screen|display|monitor) off)$", RegexOptions.IgnoreCase))
            return One("화면 끄기를 요청했어요.", "display_off");
        if (compact is "몇시야" or "지금몇시야" or "시간알려줘" or "현재시간" || Regex.IsMatch(compact, @"^(?:지금)?몇시(?:냐|니|예요|에요|인가요|입니까)$")) return One(DateTime.Now.ToString("지금은 tt h시 m분이에요.", CultureInfo.GetCultureInfo("ko-KR")), "answer");
        if (compact is "오늘며칠이야" or "오늘날짜" or "날짜알려줘") return One(DateTime.Now.ToString("오늘은 M월 d일 dddd이에요.", CultureInfo.GetCultureInfo("ko-KR")), "answer");
        if (compact is "받아써" or "받아쓰기" or "받아쓰기시작") return One("받아쓰기를 시작했어요. 입력할 창을 선택한 뒤 말하세요. 끝내려면 ‘그만’이라고 하세요.", "dictation");
        var dictated = Regex.Match(input.Trim(), @"^받아적어\s+([\s\S]+)$");
        if (dictated.Success) return One("", "dictation_once", dictated.Groups[1].Value);
        if (compact is "타이머취소" or "알람취소") return One("타이머를 취소했어요.", "cancel_timers");
        if (compact is "타이머목록" or "타이머확인" or "남은타이머") return One("타이머 목록을 확인했어요.", "list_timers");
        if (compact is "음소거" or "소리꺼") return One("출력 소리를 껐어요.", "media", "mute_on");
        if (compact == "음소거해제") return One("출력 음소거를 해제했어요.", "media", "unmute");
        if (compact is "소리키워" or "볼륨올려" or "소리올려") return One("소리를 높였어요.", "volume_delta", number: 10);
        if (compact is "소리줄여" or "볼륨내려" or "소리낮춰") return One("소리를 낮췄어요.", "volume_delta", number: -10);
        if (compact is "일시정지" or "음악멈춰" or "영상멈춰") return One("미디어 일시정지를 요청했어요.", "media", "pause");
        if (compact is "음악재생" or "다시재생" or "영상계속틀어") return One("미디어 재생을 요청했어요.", "media", "play");
        if (compact is "다음영상" or "다음곡") return One("다음 미디어로 넘겼어요.", "media", "next");
        if (compact is "뒤로가" or "이전페이지") return One("이전 페이지로 이동했어요.", "browser", "back");
        if (compact is "앞으로가") return One("다음 페이지로 이동했어요.", "browser", "forward");
        if (compact is "새로고침") return One("페이지를 새로고침했어요.", "browser", "refresh");
        if (compact is "되돌려" or "방금거취소" or "실행취소") return One("", "undo");
        if (compact is "방금뭐했어" or "방금뭘했어" or "실행기록" or "최근동작") return One("", "history");
        if (compact is "네가연창닫아" or "방금연창닫아" or "비서가연창닫아") return One("", "close_owned");
        if (compact is "엔터" or "엔터쳐") return One("Enter 키를 눌렀어요.", "key", "enter");
        // These requests depend on observations; do not turn the entire sentence
        // into a YouTube query or prematurely execute just its first duration.
        if (RequiresObservations(s)) return null;
        if (WebCommands.Browser(s) is { } browserCommand) return browserCommand;
        if (Regex.IsMatch(s, @"^(?:선택한\s*(?:글|문장|텍스트)|이\s*글|이\s*문장).*(?:다듬|고쳐|번역|줄여|요약|바꿔)")) return One("선택한 글을 바꿨어요.", "rewrite", s);
        Match m;
        if (compact is "유튜브꺼" or "유튜브창꺼" or "유튜브창닫아" or "영상닫아") return One("비서의 영상 창을 닫았어요.", "youtube_close");
        if ((m=Regex.Match(compact,@"^(?:(\d{1,3})(초|분))?(빨리감기|앞으로감기|되감기)(?:해|해줘)?$")).Success)
        {
            var seconds=m.Groups[1].Success?int.Parse(m.Groups[1].Value)*(m.Groups[2].Value=="분"?60:1):10;
            return One("영상 재생 위치 변경을 요청했어요.","seek",number:m.Groups[3].Value=="되감기"?-seconds:seconds);
        }
        if ((m = Regex.Match(s, @"^(?:소리|볼륨)\s*(\d{1,3})(?:\s*퍼센트|%)?(?:로|으로)?(?:\s*(?:해|설정해|맞춰|줘|해줘|해 줘))*$")).Success) return One($"볼륨을 {Math.Clamp(int.Parse(m.Groups[1].Value), 0, 100)}%로 맞췄어요.", "volume", number: Math.Clamp(int.Parse(m.Groups[1].Value), 0, 100));
        if ((m = Regex.Match(s, @"^(.+?)\s*라고\s*(?:써\s*줘|적어\s*줘|입력해\s*줘|써|입력해)$")).Success) return One("입력했어요.", "type", m.Groups[1].Value);
        if ((m = Regex.Match(s, @"^(?:입력해|써줘|써 줘|type)\s+(.+)$", RegexOptions.IgnoreCase)).Success) return One("입력했어요.", "type", m.Groups[1].Value);
        if ((m = Regex.Match(s, @"^(.+?)\s*(?:열어|켜|실행해)(?:\s*줘|\s*주세요)?$")).Success && Apps.TryGetValue(m.Groups[1].Value.Trim(), out var app)) return One(m.Groups[1].Value + "을 열었어요.", "open_app", app);
        if ((m = Regex.Match(s, @"^open\s+(.+)$", RegexOptions.IgnoreCase)).Success && Apps.TryGetValue(m.Groups[1].Value, out app)) return One("앱을 열었어요.", "open_app", app);
        if ((m = Regex.Match(s, @"^(.+?)\s*(?:열어|켜|실행해)(?:\s*줘|\s*주세요)?$")).Success && registry.Resolve(m.Groups[1].Value) is { } custom) return One(custom.Name + "을 열었어요.", "open_app", custom.Id);
        if ((m = Regex.Match(s, @"^open\s+(.+)$", RegexOptions.IgnoreCase)).Success && registry.Resolve(m.Groups[1].Value) is { } englishApp) return One(englishApp.Name + "을 열었어요.", "open_app", englishApp.Id);
        if (YouTubeCommands.OpensHome(s)) return One("유튜브를 열었어요.", "open_url", "https://www.youtube.com/");
        if (compact is "유튜브검색결과" or "유튜브결과보여줘" or "영상목록") return One("유튜브 검색 결과예요.", "youtube_list");
        if ((m=Regex.Match(compact.Replace("첫영상","첫번째영상"),@"^(?:유튜브)?(첫|한|두|세|네|다섯|여섯|일곱|여덟|아홉|열|[0-9]{1,2})(?:번째|째|번)(?:영상|결과|거|것)?(?:틀어|재생해|켜|보여줘|선택해)?(?:줘)?$")).Success)
        {
            var ordinals=new Dictionary<string,int>{{"첫",1},{"한",1},{"두",2},{"세",3},{"네",4},{"다섯",5},{"여섯",6},{"일곱",7},{"여덟",8},{"아홉",9},{"열",10}};
            var number=ordinals.TryGetValue(m.Groups[1].Value,out var ordinal)?ordinal:int.Parse(m.Groups[1].Value);
            return One("선택한 영상의 재생을 요청했어요.","youtube_select",number:number);
        }
        if (YouTubeCommands.Query(s) is { } videoQuery) return videoQuery;
        if((m=Regex.Match(s,@"^(.+?)\s*(?:틀어|재생해)(?:\s*줘)?$")).Success)return One("첫 번째 영상의 재생을 요청했어요.","youtube_play",m.Groups[1].Value);
        if (WebCommands.Search(s, searches) is { } webCommand) return webCommand;
        if ((m = Regex.Match(s, @"^(.+?)\s*(?:가는\s*길|지도(?:\s*찾아)?)$")).Success) return One("네이버 지도를 열었어요.", "open_url", "https://map.naver.com/p/search/" + Uri.EscapeDataString(m.Groups[1].Value));
        var timeText = s;
        var numerals = new Dictionary<string, string> { { "한", "1" }, { "일", "1" }, { "두", "2" }, { "이", "2" }, { "세", "3" }, { "삼", "3" }, { "네", "4" }, { "사", "4" }, { "오", "5" }, { "육", "6" }, { "칠", "7" }, { "팔", "8" }, { "구", "9" }, { "십", "10" } };
        timeText = Regex.Replace(timeText, @"(한|일|두|이|세|삼|네|사|오|육|칠|팔|구|십)\s*(?=시간|분|초)", x => numerals[x.Groups[1].Value]);
        var ts = Regex.Matches(timeText, @"(\d{1,4})\s*(시간|분|초)");
        if (ts.Count > 0 && !Regex.IsMatch(timeText, @"[가-힣]시(?:에|\s)") && (Regex.IsMatch(s, @"타이머|알려|뒤|후|라면") || Regex.IsMatch(timeText, @"^\d+\s*(?:시간|분|초)$")))
        {
            var seconds = ts.Sum(x => int.Parse(x.Groups[1].Value) * (x.Groups[2].Value switch { "시간" => 3600, "분" => 60, _ => 1 }));
            if (seconds > 0 && seconds <= 86400) return One($"{string.Join(" ", ts.Select(x => x.Value))} 타이머를 시작했어요.", "timer", s, seconds);
        }
        return null;
    }
}

public static class ActionPolicy
{
    public static readonly string[] AiKinds = { "kakao_chats", "kakao_draft", "kakao_send", "kakao_send_draft", "display_off", "list_sessions", "switch_session", "delegate_session", "answer", "open_app", "open_url", "type", "key", "tap", "list_windows", "select_window", "front", "list_controls", "press_control", "fill_field", "insert_field", "screenshot", "click", "volume", "volume_delta", "volume_status", "media", "browser", "browser_tabs", "browser_select", "browser_close", "browser_close_site", "mail_select", "timer", "list_timers", "cancel_timer", "cancel_timers", "current_time", "read_screen", "youtube_find", "youtube_play", "youtube_search", "youtube_select", "youtube_list", "youtube_close", "seek" };
    public static void ValidateAi(AssistantAction action)
    {
        if (!AiKinds.Contains(action.Kind)) throw new InvalidOperationException("AI에 제공하지 않은 동작이라 실행하지 않았어요: " + action.Kind);
        Validate(action);
    }
    static readonly HashSet<string> Kinds = new() { "answer", "greet", "stop", "dictation", "cancel_timers", "list_timers", "timer", "volume", "volume_delta", "media", "browser", "key", "type", "rewrite", "open_app", "open_url", "tap", "remember", "save_routine", "forget_routine", "delegate", "delegate_codex", "youtube_play", "youtube_search", "youtube_select", "youtube_list", "youtube_close", "seek" };
    public static void Validate(AssistantAction a)
    {
        if (a.Kind == "display_off" && (a.Value.Length != 0 || a.Target.Length != 0 || a.Number != 0 || a.X != 0 || a.Y != 0)) throw new InvalidOperationException("화면 끄기에는 추가 인수를 지정할 수 없어요.");
        if (a.Kind == "kakao_chats" && (a.Value.Length > 120 || a.Number is < 0 or > 240)) throw new InvalidOperationException("카카오톡 검색어나 페이지를 확인하세요.");
        if (a.Kind is "kakao_draft" or "kakao_send" or "kakao_send_draft")
        {
            if (!Guid.TryParseExact(a.Target, "N", out var chatId) || chatId == Guid.Empty || a.Number != 0) throw new InvalidOperationException("조회한 카카오톡 대화방 ID가 필요해요.");
            if (a.Kind == "kakao_send_draft" ? a.Value.Length != 0 : string.IsNullOrWhiteSpace(a.Value)) throw new InvalidOperationException("카카오톡 메시지 내용을 확인하세요.");
        }
        if (a.Kind == "list_sessions" && (a.Value.Length > 120 || a.Number is < 0 or > 240)) throw new InvalidOperationException("Claude 세션 검색어나 페이지를 확인하세요.");
        if (a.Kind is "switch_session" or "delegate_session")
        {
            if (!Guid.TryParseExact(a.Kind == "delegate_session" ? a.Target : a.Value, "N", out var sessionId) || sessionId == Guid.Empty || a.Number != 0) throw new InvalidOperationException("조회한 Claude 세션 ID가 필요해요.");
            if (a.Kind == "delegate_session" && string.IsNullOrWhiteSpace(a.Value)) throw new InvalidOperationException("맡길 작업을 입력하세요.");
        }
        if (a.Kind != "click" && (a.X != 0 || a.Y != 0)) throw new InvalidOperationException("이 동작에는 화면 좌표를 사용할 수 없어요.");
        if (a.Kind == "screenshot" && (a.Value.Length != 0 || a.Number != 0)) throw new InvalidOperationException("화면 이미지 요청에는 추가 인수를 넣지 마세요.");
        if (a.Kind == "insert_field" && a.Value.Length == 0) throw new InvalidOperationException("추가할 글이 없어요.");
        if (a.Kind == "front" && (a.Value.Length != 0 || a.Number != 0)) throw new InvalidOperationException("현재 창 조회에는 추가 인수가 필요하지 않아요.");
        if (a.Kind == "list_windows" && (a.Value.Length > 120 || a.Number is < 0 or > 240)) throw new InvalidOperationException("창 목록의 검색어나 페이지를 확인하세요.");
        if (a.Kind == "select_window" && (!Guid.TryParseExact(a.Value, "N", out var windowId) || windowId == Guid.Empty || a.Number != 0)) throw new InvalidOperationException("방금 조회한 창 ID가 필요해요.");
        if (a.Kind == "click" && (!Guid.TryParseExact(a.Value, "N", out var screenshotId) || screenshotId == Guid.Empty || a.Number != 0 || a.X is < 0 or >= 1280 || a.Y is < 0 or >= 1280)) throw new InvalidOperationException("방금 확인한 이미지 ID와 이미지 안의 좌표가 필요해요.");
        if (a.Target == null || a.Target.Length > 80 || a.Kind is not ("fill_field" or "insert_field" or "delegate_session" or "kakao_draft" or "kakao_send" or "kakao_send_draft") && a.Target.Length != 0) throw new InvalidOperationException("화면 항목 지정 값이 올바르지 않아요.");
        if (a.Kind is "list_controls" or "press_control" or "fill_field" or "insert_field")
        {
            if (a.Kind == "list_controls" && (a.Value.Length > 120 || a.Number is < 0 or > 240)) throw new InvalidOperationException("화면 목록의 검색어나 페이지를 확인하세요.");
            if (a.Kind != "list_controls" && (!Guid.TryParseExact(a.Kind is "fill_field" or "insert_field" ? a.Target : a.Value, "N", out var id) || id == Guid.Empty || a.Number != 0)) throw new InvalidOperationException("방금 조회한 화면 항목의 ID가 필요해요.");
        }
        if (a.Kind == "dictation_once") { if (string.IsNullOrWhiteSpace(a.Value) || a.Value.Length > 6000) throw new InvalidOperationException("받아적을 내용은 1~6,000자로 말하세요."); return; }
        if (a.Kind is "undo" or "history" or "close_owned") return;
        if (a.Kind is "browser_select_nth" or "browser_close_nth") { if (a.Number is < 1 or > 100 || a.Value.Length != 0) throw new InvalidOperationException("탭 번호는 1~100 사이로 지정하세요."); return; }
        if (!Kinds.Contains(a.Kind) && !AiKinds.Contains(a.Kind)) throw new InvalidOperationException("지원하지 않는 동작: " + a.Kind);
        if (a.Value.Length > 6000) throw new InvalidOperationException("한 번에 입력할 내용이 너무 깁니다.");
        if (a.Kind == "open_url" && (!Uri.TryCreate(a.Value, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(u.UserInfo))) throw new InvalidOperationException("웹 주소는 http 또는 https만 열 수 있어요.");
        if (a.Kind == "open_app" && !CommandParser.Apps.Values.Contains(a.Value) && AppRegistry.Default.Find(a.Value) == null) throw new InvalidOperationException("등록되지 않은 앱이에요: " + a.Value);
        if (a.Kind == "key") { if (a.Number != 0) throw new InvalidOperationException("키 조합은 한 번에 한 번씩 요청하세요."); _ = KeyboardChord.Parse(a.Value); }
        if (a.Kind == "browser" && a.Value is not ("back" or "forward" or "refresh")) throw new InvalidOperationException("지원하지 않는 브라우저 동작이에요.");
        if (a.Kind == "browser_tabs" && a.Value is not ("" or "all")) throw new InvalidOperationException("탭 조회 범위를 확인하세요.");
        if (a.Kind == "browser_tabs" && a.Number is < 0 or > 600) throw new InvalidOperationException("탭 목록 페이지 번호를 확인하세요.");
        if (a.Kind is "browser_select" or "browser_close" && (!Guid.TryParse(a.Value, out var tabId) || tabId == Guid.Empty)) throw new InvalidOperationException("방금 확인한 탭 목록의 ID가 필요해요.");
        if (a.Kind == "browser_close_site") _ = WebCommands.Host(a.Value);
        if (a.Kind == "mail_select" && a.Number is < 1 or > 100) throw new InvalidOperationException("메일 번호는 1~100 사이로 지정하세요.");
        if (a.Kind == "media" && a.Value is not ("play" or "pause" or "playpause" or "next" or "mute" or "mute_on" or "unmute")) throw new InvalidOperationException("지원하지 않는 미디어 동작이에요.");
        if(a.Kind=="seek"&&(a.Number==0||Math.Abs((long)a.Number)>3600))throw new InvalidOperationException("영상 이동은 1초~1시간으로 요청하세요.");
        if (a.Kind == "timer" && (a.Number <= 0 || a.Number > 86400)) throw new InvalidOperationException("타이머는 1초~24시간으로 설정하세요.");
        if (a.Kind == "cancel_timer" && (!Guid.TryParse(a.Value, out var timerId) || timerId == Guid.Empty)) throw new InvalidOperationException("타이머 목록의 정확한 ID가 필요해요.");
        if (a.Kind == "volume" && (a.Number < 0 || a.Number > 100)) throw new InvalidOperationException("볼륨은 0~100입니다.");
        if (a.Kind == "volume_delta" && Math.Abs((long)a.Number) > 100) throw new InvalidOperationException("볼륨 변경 범위를 확인하세요.");
        if (a.Kind == "tap" && string.IsNullOrWhiteSpace(a.Value)) throw new InvalidOperationException("누를 버튼 이름이 없어요.");
        if(a.Kind is "youtube_play" or "youtube_search" or "youtube_find" && (string.IsNullOrWhiteSpace(a.Value)||a.Value.Length>500))throw new InvalidOperationException("영상 검색어는 1~500자로 입력하세요.");
        if(a.Kind=="youtube_select"&&(a.Number<1||a.Number>10))throw new InvalidOperationException("영상은 1~10번째 중에서 선택하세요.");
    }
    public static bool NeedsConfirmation(AssistantAction a, bool fromAi) => a.Kind is "kakao_draft" or "kakao_send" or "kakao_send_draft" or "delegate_session" or "tap" or "press_control" or "fill_field" or "insert_field" or "screenshot" or "click" or "delegate" or "delegate_codex" or "forget_routine" || a.Kind == "key" && KeyboardChord.Parse(a.Value).NeedsConfirmation || a.Kind == "type" && (fromAi || a.Value.Contains('\n') || a.Value.Contains('\r')) || fromAi && a.Kind is "browser_close" or "browser_close_site" or "display_off";
}
