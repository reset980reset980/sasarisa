using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

public sealed record RegisteredApp(string Id, string Name, string Path);
public sealed class AppRegistry
{
    static readonly Lazy<AppRegistry> shared = new(() => new());
    public static AppRegistry Default => shared.Value;
    readonly string file;
    List<RegisteredApp> apps = new();
    bool corrupt;
    string knownStamp = "missing";
    public IReadOnlyList<RegisteredApp> Items => apps.AsReadOnly();
    public int Version { get; private set; }
    internal string StorageStamp { get { DataFileStamp.Require(file, knownStamp); return knownStamp; } }
    internal string StoragePath => file;
    internal string BackupPath => file + ".backup";
    internal void ApplySnapshot(IReadOnlyList<RegisteredApp> next, int expectedVersion, string expectedStamp)
    {
        if (corrupt) throw new InvalidOperationException(Error);
        if (expectedVersion != Version) throw new InvalidOperationException("등록 앱 목록이 바뀌었어요. 새로 가져와 확인하세요.");
        DataFileStamp.Require(file, expectedStamp); AppDataTransfer.ValidateSnapshot(next); Write(next.ToList());
    }
    public string? Error => corrupt ? "등록 앱 파일을 읽지 못했어요. apps.json과 백업 파일을 확인하세요." : null;
    public AppRegistry(string? path = null)
    {
        file = path ?? System.IO.Path.Combine(AppPaths.Home, "apps.json");
        if (!File.Exists(file)) return;
        try
        {
            var text = DataFileStamp.ReadText(file, out var loadedStamp); var loaded = JsonSerializer.Deserialize<List<RegisteredApp>>(text, SettingsStore.Json) ?? throw new JsonException();
            if (loaded.Count > 100 || loaded.Any(x => x == null || string.IsNullOrEmpty(x.Id) || !x.Id.StartsWith("custom:", StringComparison.Ordinal) || !Guid.TryParse(x.Id[7..], out _) || string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrEmpty(x.Path) || !System.IO.Path.IsPathFullyQualified(x.Path)) || loaded.Select(x => x.Id).Distinct().Count() != loaded.Count || loaded.Select(x => Key(x.Name)).Distinct().Count() != loaded.Count) throw new JsonException();
            knownStamp = loadedStamp; apps = loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException) { corrupt = true; AppPaths.Log("apps", "등록 앱 파일 읽기 실패"); }
    }
    static string Key(string name) => CommandParser.Normalize(name).Replace(" ", "").ToUpperInvariant();
    internal void Reload()
    {
        if (!corrupt && DataFileStamp.Read(file) == knownStamp) return;
        var loaded = new AppRegistry(file); if (loaded.Error != null) throw new InvalidOperationException(loaded.Error);
        apps = loaded.apps; knownStamp = loaded.knownStamp; corrupt = false; Version++;
    }
    public RegisteredApp? Resolve(string name) => apps.FirstOrDefault(x => Key(x.Name) == Key(name));
    public RegisteredApp? Find(string id) => apps.FirstOrDefault(x => x.Id == id);
    public static void ValidatePath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length > 1024 ||
            System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".lnk") || !File.Exists(path))
            throw new ArgumentException("이 PC에 있는 실행 파일(.exe) 또는 바로가기(.lnk)를 선택하세요.");
    }
    public RegisteredApp Save(string name, string path, string? existingId = null)
    {
        if (corrupt) throw new InvalidOperationException(Error);
        name = CommandParser.Normalize(name); path = path.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || name.Any(char.IsControl)) throw new ArgumentException("앱 이름은 1~40자로 입력하세요.");
        if (CommandParser.Apps.Keys.Any(x => Key(x) == Key(name))) throw new ArgumentException("기본 앱과 다른 이름을 사용하세요.");
        if (apps.Any(x => x.Id != existingId && Key(x.Name) == Key(name))) throw new ArgumentException("이미 등록된 이름이에요. 목록에서 선택해 수정하세요.");
        if (existingId != null && Find(existingId) == null) throw new ArgumentException("수정할 앱을 목록에서 다시 선택하세요.");
        ValidatePath(path);
        var item = new RegisteredApp(existingId ?? "custom:" + Guid.NewGuid().ToString("N"), name, System.IO.Path.GetFullPath(path));
        var next = apps.Where(x => x.Id != item.Id).Append(item).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (next.Count > 100) throw new ArgumentException("앱은 최대 100개까지 등록할 수 있어요.");
        Write(next); return item;
    }
    public void Remove(string id)
    {
        if (corrupt) throw new InvalidOperationException(Error);
        Write(apps.Where(x => x.Id != id).ToList());
    }
    void Write(List<RegisteredApp> next)
    {
        DataFileStamp.Require(file, knownStamp);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var text = JsonSerializer.Serialize(next, SettingsStore.Json); File.WriteAllText(temp, text);
            if (File.Exists(file)) File.Replace(temp, file, file + ".backup"); else File.Move(temp, file);
            apps = next; knownStamp = DataFileStamp.Text(text); Version++;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
