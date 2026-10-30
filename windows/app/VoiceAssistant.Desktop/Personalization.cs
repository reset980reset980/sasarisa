using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

public sealed record PersonalState
{
    public Dictionary<string, List<AssistantAction>> Routines { get; set; } = new();
    public Dictionary<string, CommandPlan> Learned { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public Dictionary<string, MacLearningInfo> MacLearning { get; set; } = new();
    public long FastHits { get; set; }
}
public sealed record MacLearningInfo(long Uses, double Learned, bool Automatic = false);

public sealed partial class Personalization
{
    PersonalState state = new(); string lastLearned = ""; readonly string FilePath; string knownStamp = "missing";
    public string? Error { get; private set; }
    public int Version { get; private set; }
    internal string StorageStamp { get { DataFileStamp.Require(FilePath, knownStamp); return knownStamp; } }
    internal string StoragePath => FilePath;
    internal string BackupPath => FilePath + ".backup";
    static readonly HashSet<string> Reusable = new() { "display_off", "open_app", "open_url", "volume", "volume_delta", "media", "browser", "timer", "cancel_timers", "youtube_play", "youtube_search" };
    public Personalization(string? file = null, Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        FilePath = file ?? Path.Combine(AppPaths.Home, "personal.json");
        if (!File.Exists(FilePath)) return;
        try { var text = DataFileStamp.ReadText(FilePath, out var loadedStamp); var loaded = JsonSerializer.Deserialize<PersonalState>(text, SettingsStore.Json) ?? throw new JsonException(); ValidateState(loaded); knownStamp = loadedStamp; state = loaded; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Error = "저장된 기억·루틴 파일을 읽지 못했어요. personal.json과 .backup 파일을 확인하세요. 기존 파일은 덮어쓰지 않습니다."; }
    }
    static PersonalState Copy(PersonalState value) => JsonSerializer.Deserialize<PersonalState>(JsonSerializer.Serialize(value))!;
    public PersonalState Snapshot() => Copy(state);
    internal void Reload()
    {
        if (Error == null && DataFileStamp.Read(FilePath) == knownStamp) return;
        var loaded = new Personalization(FilePath, clock);
        if (loaded.Error != null) throw new InvalidOperationException(loaded.Error);
        state = loaded.state; knownStamp = loaded.knownStamp; Error = null; ClearRecentLearning(); Version++;
    }
    internal void ApplySnapshot(PersonalState next, int expectedVersion, string expectedStamp)
    {
        RequireVersion(expectedVersion); DataFileStamp.Require(FilePath, expectedStamp); ValidateState(next);
        Update(s => { var copy = Copy(next); s.Notes = copy.Notes; s.Routines = copy.Routines; s.Learned = copy.Learned; s.MacLearning = copy.MacLearning; s.FastHits = copy.FastHits; }); ClearRecentLearning();
    }
    static void ValidateState(PersonalState value)
    {
        if (value.FastHits < 0 || value.Routines == null || value.Learned == null || value.Notes == null || value.MacLearning == null || value.MacLearning.Count > 1000 || value.MacLearning.Any(x => !value.Learned.ContainsKey(x.Key) || x.Value == null || x.Value.Uses < 0 || !double.IsFinite(x.Value.Learned)) || value.Routines.Count > 1000 || value.Learned.Count > 1000 || value.Notes.Count > 1000 || value.Notes.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 4000)) throw new JsonException("Invalid personal data");
        void Actions(IReadOnlyCollection<AssistantAction>? actions) { if (actions == null || actions.Count is < 1 or > 8 || !CanReuse(actions)) throw new JsonException("Invalid stored actions"); }
        foreach (var row in value.Routines) { if (string.IsNullOrWhiteSpace(row.Key) || row.Key.Length > 30) throw new JsonException(); Actions(row.Value); }
        foreach (var row in value.Learned) { if (string.IsNullOrWhiteSpace(row.Key) || row.Key.Length > 6000 || row.Value == null || row.Value.Reply == null || row.Value.Reply.Length > 12000) throw new JsonException(); Actions(row.Value.Actions?.ToArray()); }
    }
    public static bool CanReuse(IEnumerable<AssistantAction> actions)
    {
        var list = actions.ToArray();
        if (list.Length == 0) return false;
        foreach (var action in list)
        {
            if (action == null || action.Value == null || action.Value.Length > 4000) return false;
            if (Reusable.Contains(action.Kind)) continue;
            try { ScreenLearning.ValidateStored(action); } catch (ArgumentException) { return false; }
        }
        return true;
    }
    void Update(Action<PersonalState> update)
    {
        if (Error != null) throw new InvalidOperationException(Error);
        DataFileStamp.Require(FilePath, knownStamp);
        var next = Copy(state); update(next); ValidateState(next);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var text = JsonSerializer.Serialize(next, SettingsStore.Json); var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".backup"); else File.Move(temp, FilePath);
            state = next; knownStamp = DataFileStamp.Text(text); Version++;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    void RequireVersion(int expected) { if (expected != Version) throw new InvalidOperationException("다른 명령으로 저장 내용이 바뀌었어요. 새로고침 후 다시 선택하세요."); }
    public void Restore(PersonalState previous, int expectedVersion)
    { RequireVersion(expectedVersion); Update(s => { var copy = Copy(previous); s.Notes = copy.Notes; s.Routines = copy.Routines; s.Learned = copy.Learned; s.MacLearning = copy.MacLearning; s.FastHits = copy.FastHits; }); ClearRecentLearning(); }
    static string Key(string text) => CommandParser.Normalize(text).Replace(" ", "").ToUpperInvariant();
    public void SaveNote(int? index, string text, int expectedVersion)
    {
        RequireVersion(expectedVersion); text = text.Trim();
        if (text.Length is < 1 or > 2000) throw new ArgumentException("기억할 내용은 1~2,000자로 입력하세요.");
        if (index.HasValue && (index < 0 || index >= state.Notes.Count)) throw new InvalidOperationException("수정할 기억을 다시 선택하세요.");
        Update(s => { if (index.HasValue) s.Notes[index.Value] = text; else { if (s.Notes.Count >= 100) throw new ArgumentException("기억은 최대 100개입니다. 기존 내용을 정리하세요."); s.Notes.Add(text); } });
    }
    static List<AssistantAction> ValidActions(IEnumerable<AssistantAction> actions)
    {
        var result = actions.ToList(); if (result.Count is < 1 or > 8 || !CanReuse(result)) throw new ArgumentException("지원하는 앱·검색·소리·타이머·학습한 화면 동작을 1~8개 넣으세요.");
        foreach (var action in result) { if (ScreenLearning.IsStored(action)) ScreenLearning.ValidateStored(action); else ActionPolicy.Validate(action); } return result;
    }
    public void SaveRoutine(string name, IEnumerable<AssistantAction> actions, string? original, int expectedVersion)
    {
        RequireVersion(expectedVersion); name = CommandParser.Normalize(name);
        if (name.Length is < 1 or > 30 || name.Any(char.IsControl)) throw new ArgumentException("루틴 이름은 1~30자로 입력하세요.");
        if (original != null && !state.Routines.ContainsKey(original)) throw new InvalidOperationException("수정할 루틴을 다시 선택하세요.");
        if (state.Routines.Keys.Any(x => x != original && Key(x) == Key(name))) throw new ArgumentException("같은 이름의 루틴이 있어요.");
        var list = ValidActions(actions);
        Update(s => { if (original != null) s.Routines.Remove(original); if (s.Routines.Count >= 100) throw new ArgumentException("루틴은 최대 100개입니다."); s.Routines[name] = list; });
    }
    public void SaveLearned(string phrase, string reply, IEnumerable<AssistantAction> actions, string? original, int expectedVersion)
    {
        RequireVersion(expectedVersion); phrase = CommandParser.Normalize(phrase); reply = reply.Trim();
        if (phrase.Length is < 1 or > 200 || reply.Length is < 1 or > 2000) throw new ArgumentException("호출 문장은 1~200자, 답변은 1~2,000자로 입력하세요.");
        if (original != null && !state.Learned.ContainsKey(original)) throw new InvalidOperationException("수정할 학습 내용을 다시 선택하세요.");
        if (state.Learned.Keys.Any(x => x != original && Key(x) == Key(phrase))) throw new ArgumentException("같은 호출 문장이 있어요.");
        var list = ValidActions(actions);
        Update(s => { MacLearningInfo? info = null; if (original != null) { s.Learned.Remove(original); if (s.MacLearning.Remove(original, out var saved)) info = saved; } if (s.Learned.Count >= 100) throw new ArgumentException("배운 실행 방법은 최대 100개입니다."); s.Learned[phrase] = new(reply, list); s.MacLearning[phrase] = info is null ? new(0, LearningTime) : info with { Automatic = false }; });
    }
    public void Remove(string category, string key, int expectedVersion)
    {
        RequireVersion(expectedVersion);
        Update(s =>
        {
            if (category == "notes" && int.TryParse(key, out var index) && index >= 0 && index < s.Notes.Count) s.Notes.RemoveAt(index);
            else if (category == "routines" && s.Routines.Remove(key)) { }
            else if (category == "learned" && s.Learned.Remove(key)) { s.MacLearning.Remove(key); }
            else throw new ArgumentException("삭제할 항목을 다시 선택하세요.");
        });
        if (category == "learned" && lastLearned == key) ClearRecentLearning();
    }
    public static CommandPlan ParseStep(string command)
    {
        command = command.Trim();
        AssistantAction? stored = null;
        var keyCommand = Regex.Match(command, @"^(?:키|key)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var buttonCommand = Regex.Match(command, @"^(?:버튼|button)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (keyCommand.Success) stored = ScreenLearning.Input(new("key", keyCommand.Groups[1].Value.Trim()), false) ?? throw new ArgumentException("이 키는 저장 동작으로 사용할 수 없어요.");
        else if (buttonCommand.Success) stored = ScreenLearning.Control("learned_press", new("", "", buttonCommand.Groups[1].Value.Trim(), "", "", null, 0));
        if (stored != null) return new("저장한 화면 동작을 실행했어요.", new[] { stored });
        var plan = Uri.TryCreate(command, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? new CommandPlan("웹 페이지를 열었어요.", new[] { new AssistantAction("open_url", command) }) : CommandParser.Parse(command);
        if (plan == null) throw new ArgumentException("이 명령은 바로 실행 동작으로 해석하지 못했어요. 예: ‘메모장 열어’, ‘볼륨 30’, ‘3분 타이머’");
        if (plan.Actions.Count == 1 && plan.Actions[0].Kind is "type" or "key")
            plan = plan with { Actions = new[] { ScreenLearning.Input(plan.Actions[0], false) ?? throw new ArgumentException("이 입력은 저장 동작으로 사용할 수 없어요.") } };
        ValidActions(plan.Actions); return plan;
    }
    public static string DescribeAction(AssistantAction action) => action.Kind switch
    {
        _ when ScreenLearning.IsStored(action) => ScreenLearning.Describe(action),
        "display_off" => "모니터 화면 끄기",
        "open_app" => (AppRegistry.Default.Find(action.Value)?.Name ?? CommandParser.Apps.FirstOrDefault(x => x.Value == action.Value).Key ?? "이전에 등록한 앱") + " 열기",
        "open_url" => "웹 열기 · " + action.Value, "volume" => $"음량 {action.Number}%", "volume_delta" => $"음량 {(action.Number > 0 ? "+" : "")}{action.Number}%",
        "timer" => $"{action.Number}초 타이머 · {action.Value}", "cancel_timers" => "타이머 모두 취소", "youtube_play" => "유튜브 재생 · " + action.Value, "youtube_search" => "유튜브 검색 · " + action.Value,
        "browser" => action.Value switch { "back" => "브라우저 뒤로", "forward" => "브라우저 앞으로", _ => "브라우저 새로고침" },
        "media" => action.Value switch { "mute" => "음소거 전환", "next" => "다음 곡", "play" => "재생", "pause" => "일시정지", _ => "미디어 재생 전환" }, _ => "동작 확인 필요"
    };
    public string MemoryContext => string.Join("\n", state.Notes.TakeLast(20));
    public CommandPlan? Resolve(string input)
    {
        var text = CommandParser.Normalize(input); var compact = text.Replace(" ", "");
        if (compact is "기억보여줘" or "기억목록" or "뭘기억하고있어") return Answer(Error ?? (state.Notes.Count == 0 ? "저장한 기억이 없어요." : string.Join("\n", state.Notes.Select((n, i) => $"{i + 1}. {n}"))));
        if (compact is "루틴목록" or "내루틴" or "루틴보여줘") return Answer(Error ?? (state.Routines.Count == 0 ? "저장한 루틴이 없어요." : "등록한 루틴: " + string.Join(", ", state.Routines.Keys)));
        if (compact is "아니" or "그게아니야" or "틀렸어")
        {
            if (lastLearned.Length == 0) return Answer(UiText.Get("어떻게 바꿀지 말씀해 주세요."));
            if (lastLearnedPlan == null || !state.Learned.TryGetValue(lastLearned, out var recent) || !SameLearning(recent, lastLearnedPlan)) { ClearRecentLearning(); return Answer(UiText.Get("학습 내용이 바뀌어 삭제하지 않았어요. 관리 화면에서 확인해 주세요.")); }
            var key = lastLearned; Update(s => { s.Learned.Remove(key); s.MacLearning.Remove(key); }); ClearRecentLearning(); return Answer(UiText.Get("방금 배운 실행 방법을 지웠어요. 어떻게 바꿀지 말씀해 주세요."));
        }
        Match m;
        if ((m = Regex.Match(text, @"^기억해(?:줘)?\s*[:：,]?\s*(.+)$")).Success) return new("기억했어요.", new[] { new AssistantAction("remember", m.Groups[1].Value) });
        if ((m = Regex.Match(text, @"^(.+?)\s*루틴\s*(?:지워|삭제해)(?:\s*줘)?$")).Success) return new("루틴을 지웠어요.", new[] { new AssistantAction("forget_routine", m.Groups[1].Value) });
        if ((m = Regex.Match(text, @"^앞으로\s*['‘「" + "\"" + @"]?(.+?)['’」" + "\"" + @"]?\s*(?:하면|이라고\s*하면)\s*(.+)$")).Success)
        {
            var name = m.Groups[1].Value.Trim(' ', '\'', '"', '‘', '’', '「', '」'); var steps = SplitCommands(m.Groups[2].Value);
            var actions = new List<AssistantAction>();
            foreach (var step in steps) { var p = CommandParser.Parse(step); if (p == null || !CanReuse(p.Actions)) throw new InvalidOperationException("루틴에는 앱 열기·검색·볼륨·미디어·타이머 같은 빠른 명령만 넣을 수 있어요."); actions.AddRange(p.Actions); }
            if (name.Length < 1 || name.Length > 30 || actions.Count > 8) throw new InvalidOperationException("루틴 이름은 1~30자, 동작은 최대 8개입니다.");
            return new($"‘{name}’ 루틴을 저장했어요.", new[] { new AssistantAction("save_routine", JsonSerializer.Serialize(new RoutineDefinition(name, actions))) });
        }
        var match = state.Routines.ContainsKey(text) ? text : RoutineMatch(text);
        if (match != null) return new($"‘{match}’ 루틴을 실행했어요.", state.Routines[match]);
        var conditional = CommandParser.RequiresObservations(text);
        if (!conditional && state.Learned.TryGetValue(text, out var learned) && CanReuse(learned.Actions)) return learned;
        if (!conditional)
        {
            var matches = state.Learned.Where(x => Key(x.Key) == Key(text)).Take(2).ToArray();
            if (matches.Length == 1 && CanReuse(matches[0].Value.Actions)) return matches[0].Value;
        }
        if (!conditional && Regex.IsMatch(text, @"(?:열고|켜고|틀고|줄이고|그리고|한\s*다음)"))
        {
            var parts = SplitCommands(text); if (parts.Length > 1) { var plans = parts.Select(CommandParser.Parse).ToArray(); if (plans.All(p => p != null) && plans.SelectMany(p => p!.Actions).All(a => Reusable.Contains(a.Kind))) return new(string.Join(" ", plans.Select(p => p!.Reply)), plans.SelectMany(p => p!.Actions).ToList()); }
        }
        if ((m = Regex.Match(text, @"^(?:클로드(?:\s*코드)?|코딩\s*비서)(?:한테|에게|에)\s*(.+)$")).Success) return new("Claude Code 작업 창을 열었어요. 그 창에서 진행 상황과 권한 요청을 확인할 수 있어요.", new[] { new AssistantAction("delegate", m.Groups[1].Value) });
        if ((m = Regex.Match(text, @"^(?:코덱스|codex)(?:한테|에게|에)\s*(.+)$",RegexOptions.IgnoreCase)).Success) return new("Codex 작업 창을 열었어요. 그 창에서 진행 상황과 권한 요청을 확인할 수 있어요.", new[] { new AssistantAction("delegate_codex", m.Groups[1].Value) });
        if (conditional) return null;
        if ((m = Regex.Match(text, @"^(오전|오후)?\s*(\d{1,2})시(?:\s*(\d{1,2})분)?에?\s*(.+?)?\s*(?:알려\s*줘|알람|알려줘)$")).Success)
        {
            var h = int.Parse(m.Groups[2].Value); var minute = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0; if (h > 23 || minute > 59) throw new InvalidOperationException("알림 시간을 확인하세요.");
            if (m.Groups[1].Value == "오후" && h < 12) h += 12; else if (m.Groups[1].Value == "오전" && h == 12) h = 0;
            var when = DateTime.Today.AddHours(h).AddMinutes(minute); if (when <= DateTime.Now) when = when.AddDays(1);
            return new($"{when:M월 d일 HH:mm}에 알려드릴게요.", new[] { new AssistantAction("timer", m.Groups[4].Value.Length > 0 ? m.Groups[4].Value : "예약한 알림", Math.Max(1, (int)(when - DateTime.Now).TotalSeconds)) });
        }
        return null;
    }
    public void Execute(AssistantAction action)
    {
        switch (action.Kind)
        {
            case "remember": SaveNote(null, action.Value, Version); break;
            case "save_routine": var r = JsonSerializer.Deserialize<RoutineDefinition>(action.Value)!; SaveRoutine(r.Name, r.Actions, state.Routines.ContainsKey(r.Name) ? r.Name : null, Version); break;
            case "forget_routine": var key = state.Routines.Keys.FirstOrDefault(k => k.Replace(" ", "") == action.Value.Replace(" ", "")); if (key == null) throw new InvalidOperationException("등록되지 않은 루틴이에요."); Update(s => s.Routines.Remove(key)); break;
        }
    }
    public void Learn(string text, CommandPlan plan)
    {
        if (Error != null || CommandParser.RequiresObservations(text) || !CanReuse(plan.Actions) || ScreenLearning.HasScreenSteps(plan.Actions) && Key(text).Length < 4) return;
        var normalized = CommandParser.Normalize(text); var matches = state.Learned.Keys.Where(x => Key(x) == Key(normalized)).Take(2).ToArray();
        var key = state.Learned.ContainsKey(normalized) ? normalized : matches.Length == 1 ? matches[0] : normalized;
        var owned = CopyLearning(plan);
        Update(s => { s.Learned[key] = owned; s.MacLearning[key] = new(0, LearningTime, Automatic: true); while (s.Learned.Count > 100) { var oldest = s.Learned.Keys.First(); s.Learned.Remove(oldest); s.MacLearning.Remove(oldest); } }); lastLearned = key; lastLearnedPlan = CopyLearning(owned);
    }
    string? RoutineMatch(string text)
    {
        var query = Key(text); var matches = state.Routines.Keys.Where(x => Key(x) == query).Take(2).ToArray();
        if (matches.Length > 0) return matches.Length == 1 ? matches[0] : null;
        foreach (var suffix in new[] { "해줘", "시작해", "시작", "루틴", "모드", "켜", "해" })
        {
            if (!query.EndsWith(suffix, StringComparison.Ordinal) || query.Length <= suffix.Length) continue;
            var stem = query[..^suffix.Length]; matches = state.Routines.Keys.Where(x => Key(x) == stem).Take(2).ToArray();
            if (matches.Length > 0) return matches.Length == 1 ? matches[0] : null;
        }
        return null;
    }
    public static string[] SplitCommands(string text)
    {
        text = Regex.Replace(text, @"열고\s*", "열어|"); text = Regex.Replace(text, @"켜고\s*", "켜|"); text = Regex.Replace(text, @"틀고\s*", "틀어|"); text = Regex.Replace(text, @"줄이고\s*", "줄여|");
        text = Regex.Replace(text, @"\s*(?:그리고|한\s*다음)\s*", "|"); return text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
    static CommandPlan Answer(string text) => new(text, Array.Empty<AssistantAction>());
    public sealed record RoutineDefinition(string Name, List<AssistantAction> Actions);
}
