using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceAssistant.Desktop;

public sealed record PortableApp(string Id, string Name, string Hint);
public sealed record PersonalDataPackage(string Format, int Version, PersonalState Personal, PortableApp[] Apps);
public sealed record PersonalImportSource(PersonalDataPackage Package, string[] Notices);
public sealed record PersonalImportOptions(bool Notes = true, bool Routines = true, bool Learned = true, bool ReplaceConflicts = false);
public sealed record PersonalImportDraft(PersonalState Next, string Description, string[] Blocked, int Added, int Replaced, int Skipped);

public static class PersonalDataTransfer
{
    public const string Format = "Sasarisa.Windows.Personal";
    public const int MaxBytes = 64 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(SettingsStore.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 20, RespectRequiredConstructorParameters = true };
    internal static string Key(string value) => CommandParser.Normalize(value).Replace(" ", "").ToUpperInvariant();
    internal static bool Custom(string id) => !string.IsNullOrEmpty(id) && id.StartsWith("custom:", StringComparison.Ordinal) && Guid.TryParse(id[7..], out _);
    internal static string ReadText(string path, int limit = MaxBytes)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new ArgumentException(UiText.Get("가져올 파일이 너무 커요. 개인 데이터는 64 MB 이내로 선택하세요."));
        using var reader = new StreamReader(input, new UTF8Encoding(false, true)); return reader.ReadToEnd();
    }
    internal static void WriteText(string path, string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new ArgumentException(UiText.Get("내보낼 개인 데이터는 64 MB 이내여야 합니다."));
        var full = Path.GetFullPath(path); var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); if (File.Exists(full)) File.Replace(temp, full, full + ".previous"); else File.Move(temp, full); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void Name(string value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl) || CommandParser.Normalize(value).Length == 0) throw new ArgumentException(UiText.Get("호출 이름이나 문장의 길이·문자를 확인하세요."));
    }
    internal static void ValidateAction(AssistantAction action, ISet<string> appIds, bool allowMissingApps = false)
    {
        if (action == null || action.Value == null || action.Value.Length > 4000 || !Personalization.CanReuse(new[] { action })) throw new ArgumentException(UiText.Get("지원하는 앱·검색·소리·타이머·학습한 화면 동작을 1~8개 넣으세요."));
        if (ScreenLearning.IsStored(action)) ScreenLearning.ValidateStored(action);
        else if (action.Kind == "open_app" && Custom(action.Value))
        {
            if (!allowMissingApps && !appIds.Contains(action.Value)) throw new ArgumentException(UiText.Get("등록 앱의 연결 정보가 빠졌어요."));
            ActionPolicy.Validate(action with { Value = "notepad" });
        }
        else ActionPolicy.Validate(action);
    }
    static void ValidateActions(IReadOnlyList<AssistantAction> actions, ISet<string> appIds, bool allowMissingApps)
    {
        if (actions == null || actions.Count is < 1 or > 8) throw new ArgumentException(UiText.Get("루틴과 배운 실행 방법에는 동작을 1~8개 넣으세요."));
        foreach (var action in actions) ValidateAction(action, appIds, allowMissingApps);
    }
    internal static void ValidateState(PersonalState state, ISet<string> appIds, bool allowMissingApps = false)
    {
        if (state == null || state.Notes == null || state.Routines == null || state.Learned == null || state.MacLearning == null || state.MacLearning.Count > 1000 || state.Notes.Count > 1000 || state.Routines.Count > 1000 || state.Learned.Count > 1000) throw new ArgumentException(UiText.Get("개인 데이터의 종류나 개수가 올바르지 않아요."));
        foreach (var info in state.MacLearning) if (!state.Learned.ContainsKey(info.Key) || info.Value == null || info.Value.Uses < 0 || !double.IsFinite(info.Value.Learned)) throw new ArgumentException(UiText.Get("Mac 학습 기록의 호출 문장·횟수·시각을 확인하세요."));
        foreach (var note in state.Notes) if (string.IsNullOrWhiteSpace(note) || note.Length > 4000 || note.Contains('\0')) throw new ArgumentException(UiText.Get("기억할 내용은 1~4,000자로 작성하세요."));
        if (state.Routines.Keys.Select(Key).Distinct().Count() != state.Routines.Count || state.Learned.Keys.Select(Key).Distinct().Count() != state.Learned.Count) throw new ArgumentException(UiText.Get("같은 호출 이름을 가진 항목이 여러 개예요."));
        foreach (var row in state.Routines) { Name(row.Key, 30); ValidateActions(row.Value, appIds, allowMissingApps); }
        foreach (var row in state.Learned)
        {
            Name(row.Key, 6000);
            if (row.Value == null || row.Value.Reply == null || row.Value.Reply.Length > 12000) throw new ArgumentException(UiText.Get("배운 실행 방법의 답변을 확인하세요."));
            ValidateActions(row.Value.Actions, appIds, allowMissingApps);
        }
    }
    static void Validate(PersonalDataPackage package)
    {
        if (package.Format != Format || package.Version != 1 || package.Apps == null || package.Apps.Length > 100) throw new ArgumentException(UiText.Get("지원하는 개인 데이터 파일이 아니에요."));
        foreach (var app in package.Apps)
        {
            if (app == null || !Custom(app.Id) || app.Hint == null || app.Hint.Length > 4000) throw new ArgumentException(UiText.Get("등록 앱 연결 정보가 올바르지 않아요."));
            Name(app.Name, 120);
        }
        if (package.Apps.Select(x => x.Id).Distinct().Count() != package.Apps.Length) throw new ArgumentException(UiText.Get("등록 앱 연결 ID가 중복돼요."));
        ValidateState(package.Personal, package.Apps.Select(x => x.Id).ToHashSet());
    }
    public static PersonalDataPackage Create(PersonalState state, IEnumerable<RegisteredApp> apps)
    {
        var copy = JsonSerializer.Deserialize<PersonalState>(JsonSerializer.Serialize(state), Json)!;
        ValidateState(copy, new HashSet<string>(), allowMissingApps: true);
        var ids = copy.Routines.Values.SelectMany(x => x).Concat(copy.Learned.Values.SelectMany(x => x.Actions)).Where(x => x.Kind == "open_app" && Custom(x.Value)).Select(x => x.Value).ToHashSet();
        var references = apps.Where(x => ids.Contains(x.Id)).Select(x => new PortableApp(x.Id, x.Name, x.Path)).ToList();
        foreach (var missing in ids.Except(references.Select(x => x.Id))) references.Add(new(missing, UiText.Get("연결되지 않은 앱"), UiText.Get("이 PC에서 사용할 앱을 다시 선택하세요.")));
        var package = new PersonalDataPackage(Format, 1, copy, references.ToArray()); Validate(package); return package;
    }
    public static string Serialize(PersonalDataPackage package)
    {
        Validate(package); var text = JsonSerializer.Serialize(package, Json);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new ArgumentException(UiText.Get("개인 데이터는 64 MB 이내여야 합니다.")); return text;
    }
    public static PersonalDataPackage Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new ArgumentException(UiText.Get("개인 데이터는 64 MB 이내여야 합니다."));
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 20 }); SettingsTransfer.CheckTree(document.RootElement);
            var result = JsonSerializer.Deserialize<PersonalDataPackage>(text, Json) ?? throw new JsonException(); Validate(result); return result;
        }
        catch (JsonException ex) { throw new ArgumentException(UiText.Get("개인 데이터 파일 형식을 확인하세요. 이 앱에서 내보낸 파일을 선택하세요."), ex); }
    }
    public static PersonalDataPackage Read(string path) => Parse(ReadText(path));
    public static void Write(string path, PersonalState state, IEnumerable<RegisteredApp> apps) => WriteText(path, Serialize(Create(state, apps)));
    public static PersonalState ReadBackup(string path)
    {
        try
        {
            var text = ReadText(path); using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 20 }); SettingsTransfer.CheckTree(document.RootElement);
            foreach (var name in new[] { "Notes", "Routines", "Learned" }) if (!document.RootElement.TryGetProperty(name, out _)) throw new JsonException();
            var state = JsonSerializer.Deserialize<PersonalState>(text, Json) ?? throw new JsonException(); ValidateState(state, new HashSet<string>(), allowMissingApps: true); return state;
        }
        catch (JsonException ex) { throw new ArgumentException(UiText.Get("개인 데이터 백업 형식이 올바르지 않아요."), ex); }
    }
    public static Dictionary<string, string> SuggestMappings(PersonalDataPackage package, IReadOnlyList<RegisteredApp> apps)
    {
        var result = new Dictionary<string, string>();
        foreach (var source in package.Apps)
        {
            var matches = apps.Where(x => Key(x.Name) == Key(source.Name)).ToArray();
            if (matches.Length == 1) result[source.Id] = matches[0].Id;
            else if (CommandParser.Parse("open " + source.Name) is { Actions.Count: 1 } plan && plan.Actions[0].Kind == "open_app" && !Custom(plan.Actions[0].Value)) result[source.Id] = plan.Actions[0].Value;
        }
        return result;
    }
    public static PersonalImportDraft Prepare(PersonalDataPackage package, PersonalState current, IReadOnlyList<RegisteredApp> apps, IReadOnlyDictionary<string, string> mappings, PersonalImportOptions options)
    {
        Validate(package);
        var next = JsonSerializer.Deserialize<PersonalState>(JsonSerializer.Serialize(current), Json)!; var blocked = new List<string>(); var lines = new List<string>(); int added = 0, replaced = 0, skipped = 0;
        var validTargets = CommandParser.Apps.Values.Concat(apps.Select(x => x.Id)).ToHashSet(StringComparer.Ordinal);
        List<AssistantAction>? Map(string label, IReadOnlyList<AssistantAction> actions)
        {
            var output = new List<AssistantAction>();
            foreach (var action in actions)
            {
                if (action.Kind == "open_app" && Custom(action.Value))
                {
                    if (!mappings.TryGetValue(action.Value, out var target) || !validTargets.Contains(target)) { blocked.Add(label + UiText.Get(" · 연결할 앱을 선택하세요.")); return null; }
                    output.Add(action with { Value = target });
                }
                else output.Add(action);
            }
            return output;
        }
        if (options.Notes) foreach (var note in package.Personal.Notes)
        {
            if (next.Notes.Contains(note)) { skipped++; continue; } next.Notes.Add(note); added++; lines.Add(UiText.Get("기억 추가 · ") + note);
        }
        if (options.Routines) foreach (var row in package.Personal.Routines)
        {
            var key = next.Routines.Keys.FirstOrDefault(x => Key(x) == Key(row.Key));
            if (key != null && !options.ReplaceConflicts) { skipped++; lines.Add(UiText.Get("기존 루틴 유지 · ") + key); continue; }
            var actions = Map(UiText.Get("루틴 ‘") + row.Key + "’", row.Value); if (actions == null) continue;
            if (key != null && next.Routines[key].SequenceEqual(actions)) { skipped++; lines.Add(UiText.Get("같은 루틴 유지 · ") + key); continue; }
            next.Routines[key ?? row.Key] = actions; if (key == null) added++; else replaced++;
            lines.Add((key == null ? UiText.Get("루틴 추가 · ") : UiText.Get("루틴 교체 · ")) + row.Key + "\n  " + string.Join("\n  ", actions.Select(x => Describe(x, apps))));
        }
        if (options.Learned) foreach (var row in package.Personal.Learned)
        {
            var key = next.Learned.Keys.FirstOrDefault(x => Key(x) == Key(row.Key));
            if (key != null && !options.ReplaceConflicts) { skipped++; lines.Add(UiText.Get("기존 배운 말 유지 · ") + key); continue; }
            var actions = Map(UiText.Get("배운 말 ‘") + row.Key + "’", row.Value.Actions); if (actions == null) continue;
            var sourceInfo = package.Personal.MacLearning.GetValueOrDefault(row.Key);
            if (key != null && next.Learned[key].Reply == row.Value.Reply && next.Learned[key].Actions.SequenceEqual(actions) && (sourceInfo == null || next.MacLearning.GetValueOrDefault(key) == sourceInfo)) { skipped++; lines.Add(UiText.Get("같은 배운 말 유지 · ") + key); continue; }
            next.Learned[key ?? row.Key] = row.Value with { Actions = actions }; if (key == null) added++; else replaced++;
            if (sourceInfo != null) next.MacLearning[key ?? row.Key] = sourceInfo;
            lines.Add((key == null ? UiText.Get("배운 말 추가 · ") : UiText.Get("배운 말 교체 · ")) + row.Key + "\n  " + string.Join("\n  ", actions.Select(x => Describe(x, apps))));
            if (sourceInfo != null) lines.Add(UiText.Get("  Mac에서 사용 ") + sourceInfo.Uses + UiText.Get("회 · 원본 학습 시각 보존"));
        }
        if (next.Notes.Count > 1000 || next.Routines.Count > 1000 || next.Learned.Count > 1000) blocked.Add(UiText.Get("각 종류는 최대 1,000개까지 보관할 수 있어요."));
        return new(next, UiText.Format("추가 {0}개 · 교체 {1}개 · 기존 유지/중복 {2}개\n",added,replaced,skipped) + (lines.Count == 0 ? UiText.Get("바뀌는 항목이 없어요.") : string.Join("\n", lines)), blocked.ToArray(), added, replaced, skipped);
    }
    internal static string Describe(AssistantAction action) => Describe(action, AppRegistry.Default.Items);
    internal static string Describe(AssistantAction action, IReadOnlyList<RegisteredApp> apps) => action.Kind switch
    {
        _ when ScreenLearning.IsStored(action) => ScreenLearning.Describe(action),
        "open_app" => UiText.Get("앱 열기 · ") + (apps.FirstOrDefault(x => x.Id == action.Value)?.Name ?? CommandParser.Apps.FirstOrDefault(x => x.Value == action.Value).Key ?? UiText.Get("다시 연결할 앱")),
        "open_url" => UiText.Get("웹페이지 열기 · ") + action.Value,
        "volume" => UiText.Get("음량 ") + action.Number + "%",
        "volume_delta" => UiText.Get("음량 조절 ") + action.Number + "%",
        "media" => UiText.Get("미디어 · ") + (action.Value switch { "mute_on" => UiText.Get("음소거"), "unmute" => UiText.Get("음소거 해제"), "mute" => UiText.Get("음소거 전환"), "pause" => UiText.Get("일시 정지"), "play" => UiText.Get("재생"), "next" => UiText.Get("다음"), _ => UiText.Get("재생/일시 정지") }),
        "browser" => UiText.Get("브라우저 · ") + (action.Value switch { "back" => UiText.Get("뒤로"), "forward" => UiText.Get("앞으로"), _ => UiText.Get("새로고침") }),
        "timer" => UiText.Get("타이머 ") + action.Number + UiText.Get("초 · ") + action.Value,
        "cancel_timers" => UiText.Get("타이머 모두 취소"),
        "display_off" => UiText.Get("화면 끄기"),
        "youtube_play" => UiText.Get("유튜브 재생 · ") + action.Value,
        "youtube_search" => UiText.Get("유튜브 검색 · ") + action.Value,
        _ => action.Kind
    };
    internal static PersonalDataPackage Build(PersonalState state, IEnumerable<PortableApp> apps)
    { var result = new PersonalDataPackage(Format, 1, state, apps.ToArray()); Validate(result); return result; }
    internal static void ProtectDestination(string target, params string[] sources)
    {
        var full = Path.GetFullPath(target);
        if (sources.Append(AppPaths.ConfigFile).Any(path => new[] { path, path + ".backup", path + ".previous" }.Any(x => Path.GetFullPath(x).Equals(full, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException(UiText.Get("현재 사용 중인 저장 파일과 다른 이름·위치로 내보내세요."));
    }
}
