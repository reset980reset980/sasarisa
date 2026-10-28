using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

public static class MacPersonalTransfer
{
    public static PersonalImportSource Read(IEnumerable<string> selectedFiles)
    {
        var files = selectedFiles.Select(Path.GetFullPath).ToArray();
        if (files.Length is < 1 or > 4 || files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new ArgumentException("memory.md·routines.json·learned.json·config.json을 종류별로 하나씩 선택하세요.");
        if (files.Sum(x => new FileInfo(x).Length) > PersonalDataTransfer.MaxBytes) throw new ArgumentException("가져올 개인 데이터는 합계 64 MB 이내로 선택하세요.");
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.ToLowerInvariant() is not ("memory.md" or "routines.json" or "learned.json" or "config.json")) throw new ArgumentException("Mac 원본 파일 이름을 유지해서 선택하세요: " + name);
            texts.Add(name, PersonalDataTransfer.ReadText(file, name.Equals("config.json", StringComparison.OrdinalIgnoreCase) ? SettingsTransfer.MaxBytes : PersonalDataTransfer.MaxBytes));
        }
        return Parse(texts);
    }
    public static PersonalImportSource Parse(IReadOnlyDictionary<string, string> files)
    {
        if (files.Count is < 1 or > 4 || files.Keys.Any(x => x.ToLowerInvariant() is not ("memory.md" or "routines.json" or "learned.json" or "config.json"))) throw new ArgumentException("Mac의 memory.md·routines.json·learned.json·config.json만 선택하세요.");
        if (files.Sum(x => (long)Encoding.UTF8.GetByteCount(x.Value)) > PersonalDataTransfer.MaxBytes) throw new ArgumentException("Mac 개인 데이터는 합계 64 MB 이내로 선택하세요.");
        files = files.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var state = new PersonalState(); var refs = new List<PortableApp>(); var notices = new List<string>(); var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonDocument Document(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) > PersonalDataTransfer.MaxBytes) throw new ArgumentException("개인 데이터 파일이 너무 커요.");
            var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            try { if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Mac JSON 파일의 형식을 확인하세요."); Unique(doc.RootElement); return doc; }
            catch { doc.Dispose(); throw; }
        }
        string App(string name)
        {
            PersonalDataTransfer.Name(name, 120);
            var existing = refs.FirstOrDefault(x => PersonalDataTransfer.Key(x.Name) == PersonalDataTransfer.Key(name)); if (existing != null) return existing.Id;
            var id = "custom:" + Guid.NewGuid().ToString("N"); refs.Add(new(id, name, aliases.GetValueOrDefault(name) ?? name)); return id;
        }
        if (files.TryGetValue("config.json", out var config))
        {
            using var doc = Document(config);
            if (doc.RootElement.TryGetProperty("apps", out var apps))
            {
                if (apps.ValueKind != JsonValueKind.Object || apps.EnumerateObject().Count() > 100) throw new ArgumentException("Mac 등록 앱 목록을 확인하세요.");
                var names = new HashSet<string>();
                foreach (var property in apps.EnumerateObject())
                {
                    PersonalDataTransfer.Name(property.Name, 120);
                    if (!names.Add(PersonalDataTransfer.Key(property.Name))) throw new ArgumentException("Mac 등록 앱에 같은 호출 이름이 여러 개예요.");
                    if (property.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("Mac 앱 이름은 문자열이어야 해요.");
                    var value = property.Value.GetString()!; PersonalDataTransfer.Name(value, 120); aliases.Add(property.Name, value); App(property.Name);
                }
            }
            notices.Add("config.json에서는 등록 앱 이름만 읽었어요. 로그인·키·연결 설정·Mac 실행 경로는 가져오지 않습니다.");
        }
        if (files.TryGetValue("memory.md", out var memory))
        {
            var note = new StringBuilder();
            void Finish() { var value = note.ToString().Trim(); if (value.Length > 0) state.Notes.Add(value); note.Clear(); }
            foreach (var raw in memory.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("- ", StringComparison.Ordinal)) { Finish(); note.Append(line[2..]); }
                else if (string.IsNullOrWhiteSpace(line)) Finish();
                else { if (note.Length > 0) note.Append('\n'); note.Append(line); }
            }
            Finish(); notices.Add("memory.md의 문장과 날짜를 기억 내용으로 보존했어요. 대화 기록은 가져오지 않습니다.");
        }
        List<AssistantAction> Quick(string text)
        {
            if (CommandParser.RequiresObservations(text)) throw new ArgumentException("조건·화면 확인이 필요한 문장");
            var opened = Regex.Match(text.Trim(), @"^(?:open\s+(.+)|(.+?)\s*(?:열어|켜|실행해)(?:\s*줘|\s*주세요)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (opened.Success)
            {
                var name = (opened.Groups[1].Success ? opened.Groups[1].Value : opened.Groups[2].Value).Trim();
                if (aliases.ContainsKey(name)) return new() { new("open_app", App(name)) };
                var known = CommandParser.Parse("open " + name);
                if (known is { Actions.Count: 1 } && known.Actions[0].Kind == "open_app" && !PersonalDataTransfer.Custom(known.Actions[0].Value)) return known.Actions.ToList();
                return new() { new("open_app", App(name)) };
            }
            var plan = CommandParser.Parse(text);
            if (plan == null || plan.Actions.Count == 0 || !Personalization.CanReuse(plan.Actions)) throw new ArgumentException("Windows에서 다시 지정해야 하는 동작");
            return plan.Actions.ToList();
        }
        List<AssistantAction> Learned(string command)
        {
            var tokens = command.Split('\u001f');
            if (tokens.Length == 3 && tokens[0] == "jax")
            {
                if (tokens[1] == "open-app") return new() { new("open_app", App(tokens[2])) };
                if (tokens[1] == "open-url") return new() { new("open_url", tokens[2]) };
                if (tokens[1] == "volume" && int.TryParse(tokens[2], out var volume) && volume is >= 0 and <= 100) return new() { new("volume", Number: volume) };
            }
            throw new ArgumentException("Mac 화면·키 입력 또는 지원하지 않는 학습 동작 — Windows 관리 화면에서 다시 지정하세요");
        }
        void ImportEntries(string fileName, bool learned)
        {
            if (!files.TryGetValue(fileName, out var text)) return;
            using var doc = Document(text); if (doc.RootElement.EnumerateObject().Count() > 1000) throw new ArgumentException("Mac 저장 항목은 종류별로 최대 1,000개예요.");
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                try
                {
                    PersonalDataTransfer.Name(entry.Name, learned ? 6000 : 30);
                    var array = entry.Value;
                    if (learned && (array.ValueKind != JsonValueKind.Object || !array.TryGetProperty("commands", out array))) throw new ArgumentException("commands 목록이 없음");
                    if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 8) throw new ArgumentException("동작 개수는 1~8개여야 함");
                    var actions = new List<AssistantAction>();
                    foreach (var step in array.EnumerateArray())
                    {
                        if (step.ValueKind != JsonValueKind.String || step.GetString()!.Length > 6000) throw new ArgumentException("동작 문장을 확인해야 함");
                        actions.AddRange(learned ? Learned(step.GetString()!) : Quick(step.GetString()!));
                    }
                    if (actions.Count is < 1 or > 8) throw new ArgumentException("변환한 동작은 최대 8개여야 함");
                    foreach (var action in actions) PersonalDataTransfer.ValidateAction(action, refs.Select(x => x.Id).ToHashSet());
                    if (learned)
                    {
                        var uses = entry.Value.TryGetProperty("uses", out var count) && count.TryGetInt64(out var n) ? n : 0;
                        var when = entry.Value.TryGetProperty("learned", out var date) && date.TryGetDouble(out var seconds) ? seconds : 0;
                        if (uses < 0 || !double.IsFinite(when)) throw new ArgumentException("학습 횟수나 시각을 확인해야 함");
                        state.Learned.Add(entry.Name, new("저장한 실행 방법대로 처리했어요.", actions)); state.MacLearning.Add(entry.Name, new(uses, when));
                    }
                    else state.Routines.Add(entry.Name, actions);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { notices.Add((learned ? "배운 말 제외 · " : "루틴 제외 · ") + entry.Name + " · " + ex.Message); }
            }
        }
        ImportEntries("routines.json", false); ImportEntries("learned.json", true);
        notices.Add("가져오기는 명령을 실행하지 않아요. 루틴과 배운 말은 다음에 직접 호출할 때 사용합니다. 제외한 항목의 원본 파일은 그대로 유지합니다.");
        return new(PersonalDataTransfer.Build(state, refs), notices.ToArray());
    }
    static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in element.EnumerateObject()) { if (!keys.Add(item.Name)) throw new ArgumentException("Mac 파일에 중복된 이름이 있어요."); Unique(item.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
}
