using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

public sealed record AppDataPackage(string Format, int Version, RegisteredApp[] Apps);
public sealed class AppImportRow
{
    internal string NewId { get; } = "custom:" + Guid.NewGuid().ToString("N");
    public bool Selected { get; set; } = true;
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Hint { get; set; } = "";
}
public sealed record AppImportDraft(RegisteredApp[] Next, string Description, string[] Blocked, int Changed);

public static class AppDataTransfer
{
    public const string Format = "Sasarisa.Windows.Apps";
    internal static void ValidateSnapshot(IReadOnlyList<RegisteredApp> apps)
    {
        if (apps == null || apps.Count > 100 || apps.Any(x => x == null || !PersonalDataTransfer.Custom(x.Id) || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 40 || x.Name.Any(char.IsControl) || string.IsNullOrEmpty(x.Path) || x.Path.Length > 1024 || !System.IO.Path.IsPathFullyQualified(x.Path)) ||
            apps.Select(x => x.Id).Distinct().Count() != apps.Count || apps.Select(x => PersonalDataTransfer.Key(x.Name)).Distinct().Count() != apps.Count)
            throw new ArgumentException("등록 앱 목록의 이름·ID·경로 또는 개수가 올바르지 않아요.");
    }
    public static string Serialize(IReadOnlyList<RegisteredApp> apps)
    {
        ValidateSnapshot(apps); return JsonSerializer.Serialize(new AppDataPackage(Format, 1, apps.ToArray()), PersonalDataTransfer.Json);
    }
    public static AppDataPackage Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024) throw new ArgumentException("등록 앱 파일은 1 MB 이내로 선택하세요.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 10 }); SettingsTransfer.CheckTree(document.RootElement);
            var result = JsonSerializer.Deserialize<AppDataPackage>(text, PersonalDataTransfer.Json) ?? throw new JsonException();
            if (result.Format != Format || result.Version != 1) throw new ArgumentException("지원하는 등록 앱 파일이 아니에요.");
            ValidateSnapshot(result.Apps); return result;
        }
        catch (JsonException ex) { throw new ArgumentException("등록 앱 파일 형식을 확인하세요.", ex); }
    }
    public static AppDataPackage Read(string path) => Parse(PersonalDataTransfer.ReadText(path, 1024 * 1024));
    public static void Write(string path, IReadOnlyList<RegisteredApp> apps) => PersonalDataTransfer.WriteText(path, Serialize(apps));
    public static RegisteredApp[] ReadBackup(string path)
    {
        try
        {
            var text = PersonalDataTransfer.ReadText(path, 1024 * 1024); using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 10 }); SettingsTransfer.CheckTree(document.RootElement);
            var apps = JsonSerializer.Deserialize<RegisteredApp[]>(text, PersonalDataTransfer.Json) ?? throw new JsonException(); ValidateSnapshot(apps); return apps;
        }
        catch (JsonException ex) { throw new ArgumentException("등록 앱 백업 형식이 올바르지 않아요.", ex); }
    }
    public static AppImportRow[] Rows(IEnumerable<RegisteredApp> apps) => apps.Select(x => new AppImportRow { Name = x.Name, Path = x.Path, Hint = x.Path }).ToArray();
    public static AppImportRow[] MacRows(string configPath) => MacPersonalTransfer.Parse(new Dictionary<string, string> { ["config.json"] = PersonalDataTransfer.ReadText(configPath, SettingsTransfer.MaxBytes) }).Package.Apps.Select(x => new AppImportRow { Name = x.Name, Hint = x.Hint }).ToArray();
    public static AppImportDraft Prepare(IEnumerable<AppImportRow> rows, IReadOnlyList<RegisteredApp> current, bool replace)
    {
        var next = current.ToList(); var blocked = new List<string>(); var lines = new List<string>(); var names = new HashSet<string>(); int changed = 0;
        var selected = rows.Where(x => x.Selected).ToArray();
        foreach (var row in selected)
        {
            try
            {
                PersonalDataTransfer.Name(row.Name, 40); var name = CommandParser.Normalize(row.Name);
                if (!names.Add(PersonalDataTransfer.Key(name))) throw new ArgumentException("가져올 이름이 중복돼요.");
                var existing = next.FirstOrDefault(x => PersonalDataTransfer.Key(x.Name) == PersonalDataTransfer.Key(name));
                if (existing != null && !replace) { lines.Add("기존 앱 유지 · " + existing.Name); continue; }
                if (CommandParser.Apps.Keys.Any(x => PersonalDataTransfer.Key(x) == PersonalDataTransfer.Key(name))) throw new ArgumentException("기본 앱과 다른 호출 이름을 사용하세요.");
                AppRegistry.ValidatePath(row.Path); var path = System.IO.Path.GetFullPath(row.Path);
                if (existing != null && existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) { lines.Add("같은 앱 유지 · " + existing.Name); continue; }
                var item = new RegisteredApp(existing?.Id ?? row.NewId, existing?.Name ?? name, path);
                if (existing != null) next.Remove(existing); next.Add(item); changed++;
                lines.Add((existing == null ? "앱 추가 · " : "앱 경로 교체 · ") + item.Name + "\n  " + (existing == null ? path : existing.Path + "\n  → " + path));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { blocked.Add(row.Name + " · " + ex.Message); }
        }
        if (next.Count > 100) blocked.Add("등록 앱은 최대 100개예요.");
        return new(next.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), $"바뀌는 앱 {changed}개\n" + (lines.Count == 0 ? (selected.Length == 0 ? "선택한 항목이 없어요." : "이름과 경로를 확인하면 적용할 목록이 나타납니다.") : string.Join("\n", lines)), blocked.ToArray(), changed);
    }
}
