using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal static class PersonalDataChecks
{
    internal static int Run(List<object> rows)
    {
        int failed = 0;
        void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException) { return; } throw new Exception("Rejected operation was accepted"); }
        void Check(string name, Action run) { try { run(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var folder = Path.Combine(AppPaths.WindowsRoot, "artifacts", "personal-data-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var binary = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        string FileFor(string name) => Path.Combine(folder, name + ".json");
        var sourceId = "custom:" + Guid.NewGuid().ToString("N"); var targetId = "custom:" + Guid.NewGuid().ToString("N");
        var sourceApps = new[] { new RegisteredApp(sourceId, "작업 노트", binary) }; var targetApps = new[] { new RegisteredApp(targetId, "작업 노트", binary) };
        PersonalState Source() => new() { Notes = new() { "차는 녹차를 좋아한다", "Keep Line 1\nLine 2!" }, Routines = new() { ["작업 시작"] = new() { new("open_app", sourceId), new("volume", Number: 30) } }, Learned = new() { ["편하게 들려줘"] = new("음량을 맞췄어요.", new[] { new AssistantAction("volume", Number: 50) }) } };
        PersonalDataPackage Package() => PersonalDataTransfer.Create(Source(), sourceApps);
        Check("Personal archive preserves notes action order custom app references and detached copies", () =>
        {
            var state = Source(); var json = PersonalDataTransfer.Serialize(PersonalDataTransfer.Create(state, sourceApps)); var copy = PersonalDataTransfer.Parse(json);
            Require(copy.Personal.Notes[1] == "Keep Line 1\nLine 2!" && copy.Personal.Routines["작업 시작"][0].Value == sourceId && copy.Personal.Routines["작업 시작"][1].Number == 30 && copy.Apps.Single().Name == "작업 노트", "Archive lost data or action order");
            copy.Personal.Notes.Clear(); Require(state.Notes.Count == 2 && !json.Contains("ApiKey", StringComparison.OrdinalIgnoreCase), "Archive aliased state or added credentials");
        });
        Check("Personal import remaps source machine app IDs and preserves current conflicts by default", () =>
        {
            var current = new PersonalState { Notes = new() { "기존 기억" }, Routines = new() { ["작업시작"] = new() { new("volume", Number: 10) } } };
            var mapping = PersonalDataTransfer.SuggestMappings(Package(), targetApps); Require(mapping[sourceId] == targetId, "Did not match the registered name");
            var keep = PersonalDataTransfer.Prepare(Package(), current, targetApps, mapping, new());
            Require(keep.Next.Routines["작업시작"].Single().Number == 10 && keep.Next.Notes.Count == 3 && current.Notes.Count == 1 && keep.Skipped == 1, "Default import overwrote a conflict or source");
            var replace = PersonalDataTransfer.Prepare(Package(), current, targetApps, mapping, new(ReplaceConflicts: true));
            Require(replace.Next.Routines["작업시작"][0].Value == targetId && replace.Replaced == 1 && replace.Next.Routines.Count == 1, "Explicit replacement lost target app or duplicated a name");
        });
        Check("Missing app bindings block the selected routine without guessing an executable", () =>
        {
            var blocked = PersonalDataTransfer.Prepare(Package(), new(), Array.Empty<RegisteredApp>(), new Dictionary<string, string>(), new());
            Require(blocked.Blocked.Length == 1 && blocked.Next.Routines.Count == 0, "Unbound app was imported");
            var notes = PersonalDataTransfer.Prepare(Package(), new(), Array.Empty<RegisteredApp>(), new Dictionary<string, string>(), new(Routines: false, Learned: false));
            Require(notes.Blocked.Length == 0 && notes.Next.Notes.Count == 2 && notes.Next.Learned.Count == 0, "Unselected data blocked or changed import");
            var forged = PersonalDataTransfer.Prepare(Package(), new(), targetApps, new Dictionary<string, string> { [sourceId] = "custom:" + Guid.NewGuid().ToString("N") }, new());
            Require(forged.Blocked.Length == 1, "Unknown app target accepted");
        });
        Check("Repeated personal import is a no-op including explicit identical replacements", () =>
        {
            var package = Package(); var map = new Dictionary<string, string> { [sourceId] = sourceId };
            var draft = PersonalDataTransfer.Prepare(package, Source(), sourceApps, map, new(ReplaceConflicts: true));
            Require(draft.Added == 0 && draft.Replaced == 0 && draft.Blocked.Length == 0, "Identical import requested a rewrite");
        });
        Check("Personal archives reject unknown fields nulls duplicate names unsafe actions and invalid bounds", () =>
        {
            var json = PersonalDataTransfer.Serialize(Package());
            foreach (var bad in new[] { "{}", "{broken", json.Replace("\"Version\": 1", "\"Version\": 2"), json.Replace("\"Personal\": {", "\"Personal\": {\"ApiKey\":\"fixture-secret\","), json.Replace("\"Notes\": [", "\"notes\": [], \"Notes\": ["), json.Replace("\"Notes\": [", "\"Notes\": null, \"Discard\": ["), json.Replace("\"Kind\": \"volume\"", "\"Kind\": \"type\""), json.Replace("\"Number\": 30", "\"Number\": 300") }) Reject(() => PersonalDataTransfer.Parse(bad));
            var broken = Source(); broken.Routines["작업 시작"] = new() { new("timer", Number: 0) }; Reject(() => PersonalDataTransfer.Create(broken, sourceApps));
        });
        Check("Personal import persists atomically with a restorable previous file and no automatic actions", () =>
        {
            var path = FileFor("personal-apply"); var store = new Personalization(path); store.SaveNote(null, "원래 기억", store.Version); var original = File.ReadAllText(path);
            var draft = PersonalDataTransfer.Prepare(Package(), store.Snapshot(), targetApps, new Dictionary<string, string> { [sourceId] = targetId }, new());
            store.ApplySnapshot(draft.Next, store.Version, store.StorageStamp);
            Require(File.ReadAllText(store.BackupPath) == original && new Personalization(path).Snapshot().Routines["작업 시작"][0].Value == targetId, "Import did not persist or preserve backup");
            var backup = PersonalDataTransfer.ReadBackup(store.BackupPath); store.ApplySnapshot(backup, store.Version, store.StorageStamp);
            Require(new Personalization(path).Snapshot().Notes.Single() == "원래 기억" && store.Snapshot().Routines.Count == 0, "Persisted restore failed");
        });
        Check("Failed personal replacement preserves disk backup memory revision and removes only its own temporary file", () =>
        {
            var path = FileFor("personal-locked"); var store = new Personalization(path); store.SaveNote(null, "one", 0); store.SaveNote(null, "two", store.Version);
            var before = File.ReadAllText(path); var backup = File.ReadAllText(store.BackupPath); var version = store.Version; var stamp = store.StorageStamp;
            using (File.Open(store.BackupPath, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => store.ApplySnapshot(Source(), version, stamp));
            Require(File.ReadAllText(path) == before && File.ReadAllText(store.BackupPath) == backup && store.Version == version && store.Snapshot().Notes.Count == 2 && !Directory.EnumerateFiles(folder, "personal-locked.json.*.tmp").Any(), "Failed replace mutated state or leaked its temp");
        });
        Check("Personal preview and ordinary saves reject external changes and reload preserves those changes", () =>
        {
            var path = FileFor("personal-stale"); var store = new Personalization(path); store.SaveNote(null, "old", 0); var stamp = store.StorageStamp; var version = store.Version;
            File.WriteAllText(path, JsonSerializer.Serialize(new PersonalState { Notes = new() { "external" } }));
            Reject(() => store.ApplySnapshot(Source(), version, stamp)); Reject(() => store.SaveNote(null, "overwrite", version));
            Require(new Personalization(path).Snapshot().Notes.Single() == "external", "Stale import overwrote disk"); store.Reload();
            store.SaveNote(null, "new", store.Version); Require(new Personalization(path).Snapshot().Notes.SequenceEqual(new[] { "external", "new" }), "Reload discarded external data");
        });
        Check("Existing BOM personal and app files remain readable editable and backed up", () =>
        {
            var path = FileFor("personal-bom"); File.WriteAllText(path, JsonSerializer.Serialize(new PersonalState { Notes = new() { "BOM" } }), new UTF8Encoding(true));
            var store = new Personalization(path); Require(store.Error == null, "BOM marked corrupt"); store.SaveNote(null, "next", store.Version); Require(PersonalDataTransfer.ReadBackup(store.BackupPath).Notes.Single() == "BOM", "BOM backup lost data");
            var appPath = FileFor("apps-bom"); File.WriteAllText(appPath, JsonSerializer.Serialize(sourceApps), new UTF8Encoding(true)); var apps = new AppRegistry(appPath); apps.Save("다른 앱", binary);
            Require(AppDataTransfer.ReadBackup(apps.BackupPath).Single().Id == sourceId && apps.Items.Count == 2, "BOM app registration failed");
        });
        Check("Registration import requires a local path and keeps existing IDs when replacing a path", () =>
        {
            var row = new AppImportRow { Name = "작업 노트", Path = "C:\\not-installed-fixture\\missing.exe", Hint = "old PC" };
            Require(AppDataTransfer.Prepare(new[] { row }, Array.Empty<RegisteredApp>(), false).Blocked.Length == 1, "Missing executable accepted"); row.Path = binary;
            var first = AppDataTransfer.Prepare(new[] { row }, Array.Empty<RegisteredApp>(), false); var again = AppDataTransfer.Prepare(new[] { row }, Array.Empty<RegisteredApp>(), false);
            Require(first.Blocked.Length == 0 && first.Next.Single().Id == again.Next.Single().Id, "Preview app ID was unstable");
            var current = new[] { new RegisteredApp(targetId, row.Name, Path.Combine(folder, "old.exe")) };
            Require(AppDataTransfer.Prepare(new[] { row }, current, false).Next.Single().Path == current[0].Path, "Default replaced an existing path");
            var replace = AppDataTransfer.Prepare(new[] { row }, current, true); Require(replace.Next.Single().Id == targetId && replace.Next.Single().Path == binary, "Replacement broke saved routine IDs");
        });
        Check("Registration archives preserve metadata but reject duplicate IDs names and reserved new aliases", () =>
        {
            var copy = AppDataTransfer.Parse(AppDataTransfer.Serialize(sourceApps)); Require(copy.Apps.Single() == sourceApps[0], "App archive changed metadata");
            Reject(() => AppDataTransfer.Serialize(new[] { sourceApps[0], sourceApps[0] }));
            var invalid = AppDataTransfer.Prepare(new[] { new AppImportRow { Name = "chrome", Path = binary }, new AppImportRow { Name = "새앱", Path = "\\\\server\\app.exe" } }, Array.Empty<RegisteredApp>(), false);
            Require(invalid.Blocked.Length == 2 && invalid.Next.Length == 0, "Reserved alias or network executable accepted");
        });
        Check("Registration import and backup restore survive reopening without launching executables", () =>
        {
            var path = FileFor("apps-apply"); var apps = new AppRegistry(path); var original = apps.Save("기존 앱", binary);
            var draft = AppDataTransfer.Prepare(new[] { new AppImportRow { Name = "옮긴 앱", Path = binary } }, apps.Items, false); apps.ApplySnapshot(draft.Next, apps.Version, apps.StorageStamp);
            Require(new AppRegistry(path).Items.Count == 2 && AppDataTransfer.ReadBackup(apps.BackupPath).Single() == original, "App import or backup failed");
            apps.ApplySnapshot(AppDataTransfer.ReadBackup(apps.BackupPath), apps.Version, apps.StorageStamp); Require(new AppRegistry(path).Items.Single() == original, "App restore failed");
        });
        Check("Locked registration backup and stale registration preview cannot overwrite accepted state", () =>
        {
            var path = FileFor("apps-locked"); var apps = new AppRegistry(path); apps.Save("one", binary); apps.Save("two", binary); var text = File.ReadAllText(path); var backup = File.ReadAllText(apps.BackupPath); var version = apps.Version; var stamp = apps.StorageStamp;
            using (File.Open(apps.BackupPath, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => apps.ApplySnapshot(targetApps, version, stamp));
            Require(File.ReadAllText(path) == text && File.ReadAllText(apps.BackupPath) == backup && apps.Version == version && apps.Items.Count == 2, "Failed app replace changed data");
            File.WriteAllText(path, JsonSerializer.Serialize(sourceApps)); Reject(() => apps.ApplySnapshot(targetApps, version, stamp)); Reject(() => apps.Save("new", binary));
            apps.Reload(); Require(apps.Items.Single().Id == sourceId && !Directory.EnumerateFiles(folder, "apps-locked.json.*.tmp").Any(), "App reload or temporary cleanup failed");
        });
        Check("Mac memory dates routines custom apps and unit-separated learned commands convert from original schemas", () =>
        {
            var files = new Dictionary<string, string> { ["memory.md"] = "- 녹차를 좋아한다 (2026-10-01)\n- Line one\n  Line two\n", ["config.json"] = "{\"apps\":{\"브루\":\"Vrew\"},\"elevenLabs\":{\"apiKey\":\"fixture-do-not-copy\"}}", ["routines.json"] = "{\"작업\":[\"브루 열어\",\"볼륨 30\",\"3분 타이머\"]}", ["learned.json"] = JsonSerializer.Serialize(new Dictionary<string, object> { ["차분하게들려줘"] = new { commands = new[] { "jax\u001fvolume\u001f35", "jax\u001fopen-url\u001fhttps://example.com" }, uses = 3, learned = 12345.0 } }) };
            var parsed = MacPersonalTransfer.Parse(files); var package = parsed.Package;
            Require(package.Personal.Notes.Count == 2 && package.Personal.Notes[0].Contains("2026-10-01") && package.Personal.Notes[1].Contains("Line two") && package.Personal.Routines["작업"].Count == 3 && package.Personal.Learned["차분하게들려줘"].Actions[0].Number == 35, "Original Mac schemas lost data");
            var roundtrip = PersonalDataTransfer.Parse(PersonalDataTransfer.Serialize(package));
            Require(package.Apps.Single().Name == "브루" && package.Apps.Single().Hint == "Vrew" && roundtrip.Personal.MacLearning["차분하게들려줘"] == new MacLearningInfo(3, 12345.0) && !PersonalDataTransfer.Serialize(package).Contains("fixture-do-not-copy"), "Mac app metadata learning history or ignored credentials leaked");
        });
        Check("Unsupported Mac steps exclude the whole entry with an explanation and keep other original data", () =>
        {
            var files = new Dictionary<string, string> { ["memory.md"] = "- keep this\n", ["routines.json"] = "{\"부분실행금지\":[\"크롬 열어\",\"type hello\"],\"가능\":[\"볼륨 30\"]}", ["learned.json"] = JsonSerializer.Serialize(new Dictionary<string, object> { ["입력학습"] = new { commands = new[] { "jax\u001fvolume\u001f20", "jtype\u001fprivate text" }, uses = 0, learned = 1 } }) };
            var parsed = MacPersonalTransfer.Parse(files);
            Require(parsed.Package.Personal.Routines.Count == 1 && parsed.Package.Personal.Routines.ContainsKey("가능") && parsed.Package.Personal.Learned.Count == 0 && parsed.Package.Personal.Notes.Single() == "keep this" && parsed.Notices.Any(x => x.Contains("부분실행금지")) && parsed.Notices.Any(x => x.Contains("입력학습")), "Unsupported sequence was partially imported or silently dropped");
        });
        Check("Imported compact Mac phrases and routine suffixes resolve with normal spacing and casing", () =>
        {
            var source = MacPersonalTransfer.Parse(new Dictionary<string, string> { ["routines.json"] = "{\"Focus\":[\"볼륨 25\"],\"집중 작업\":[\"볼륨 30\"]}", ["learned.json"] = JsonSerializer.Serialize(new Dictionary<string, object> { ["차분하게들려줘"] = new { commands = new[] { "jax\u001fvolume\u001f35" }, uses = 0, learned = 1 } }) });
            var store = new Personalization(FileFor("compact")); store.ApplySnapshot(source.Package.Personal, store.Version, store.StorageStamp);
            Require(store.Resolve("차분하게 들려줘")!.Actions.Single().Number == 35 && store.Resolve("focus 모드")!.Actions.Single().Number == 25 && store.Resolve("집중작업 시작해")!.Actions.Single().Number == 30, "Imported phrases did not match actual utterance forms");
            store.Learn("차분하게 들려줘", new("changed", new[] { new AssistantAction("volume", Number: 40) }));
            Require(store.Snapshot().Learned.Count == 1 && store.Resolve("차분하게들려줘")!.Actions.Single().Number == 40, "Relearning duplicated a canonical phrase");
            store.SaveLearned("조용하게", "changed", new[] { new AssistantAction("volume", Number: 40) }, "차분하게들려줘", store.Version);
            Require(new Personalization(store.StoragePath).Snapshot().MacLearning.ContainsKey("조용하게") && !store.Snapshot().MacLearning.ContainsKey("차분하게들려줘"), "Renaming lost original Mac learning metadata");
            store.Remove("learned", "조용하게", store.Version); Require(store.Snapshot().MacLearning.Count == 0, "Deleted learning retained orphan metadata");
            var ambiguousPath = FileFor("legacy-ambiguous"); File.WriteAllText(ambiguousPath, JsonSerializer.Serialize(new PersonalState { Routines = new() { ["집중 작업"] = new() { new("volume", Number: 10) }, ["집중작업"] = new() { new("volume", Number: 20) } } }));
            var ambiguous = new Personalization(ambiguousPath); Require(ambiguous.Resolve("집중작업")!.Actions.Single().Number == 20 && ambiguous.Resolve("집 중 작 업") == null, "Legacy ambiguity did not preserve exact keys or stop guessing");
        });
        Check("Mac file import leaves original files byte-identical and bounds malformed inputs", () =>
        {
            var mac = Path.Combine(folder, "mac"); Directory.CreateDirectory(mac); var path = Path.Combine(mac, "memory.md"); File.WriteAllText(path, "- source note\n", new UTF8Encoding(true)); var before = File.ReadAllBytes(path);
            Require(MacPersonalTransfer.Read(new[] { path }).Package.Personal.Notes.Single() == "source note" && File.ReadAllBytes(path).SequenceEqual(before), "Mac source was changed");
            Reject(() => MacPersonalTransfer.Read(new[] { path, path }));
            Reject(() => MacPersonalTransfer.Parse(new Dictionary<string, string> { ["conversation.log"] = "not a supported data file" }));
            Reject(() => MacPersonalTransfer.Parse(new Dictionary<string, string> { ["routines.json"] = "{\"same\":[],\"SAME\":[]}" }));
            var big = Path.Combine(mac, "learned.json"); using (var stream = File.Create(big)) stream.SetLength(PersonalDataTransfer.MaxBytes + 1L); Reject(() => MacPersonalTransfer.Read(new[] { big }));
        });
        Check("Data exports retain previous exports and refuse active storage destinations", () =>
        {
            var path = FileFor("export"); PersonalDataTransfer.Write(path, new PersonalState { Notes = new() { "first" } }, Array.Empty<RegisteredApp>()); PersonalDataTransfer.Write(path, Source(), sourceApps);
            Require(PersonalDataTransfer.Read(path + ".previous").Personal.Notes.Single() == "first", "Previous export was not retained");
            Reject(() => PersonalDataTransfer.ProtectDestination(path, path)); Reject(() => PersonalDataTransfer.ProtectDestination(path + ".backup", path)); Reject(() => PersonalDataTransfer.ProtectDestination(AppPaths.ConfigFile, path));
            var before = File.ReadAllText(path); using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => PersonalDataTransfer.Write(path, new(), Array.Empty<RegisteredApp>()));
            Require(File.ReadAllText(path) == before && !Directory.EnumerateFiles(folder, "export.json.*.tmp").Any(), "Failed export changed data or leaked temp");
        });
        return failed;
    }
}
