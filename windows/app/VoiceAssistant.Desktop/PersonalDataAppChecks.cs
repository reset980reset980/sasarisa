using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceAssistant.Desktop;

internal static class PersonalDataAppChecks
{
    internal static int Run()
    {
        if (Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); var windows = new List<Window>(); int failed = 0;
        var folder = Path.Combine(AppPaths.Home, "profiles"); Directory.CreateDirectory(folder);
        var binary = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        SettingsStore.Save(new UiSettings { Speak = false, Desktop = new() { SetupComplete = true, StartupListening = "off" } });
        var settingsBefore = File.ReadAllText(AppPaths.ConfigFile); var listeningPath = Path.Combine(AppPaths.Home, "listening-state.json"); File.WriteAllText(listeningPath, "{\"Requested\":false}"); var listeningBefore = File.ReadAllText(listeningPath);
        void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return; } throw new Exception("Rejected UI operation was accepted"); }
        T Track<T>(T view) where T : Window { windows.Add(view); return view; }
        void Hidden()
        {
            foreach (var view in windows) Require(!view.IsVisible && PresentationSource.FromVisual(view) == null, "A window obtained a native presentation surface");
            Require(File.ReadAllText(AppPaths.ConfigFile) == settingsBefore && File.ReadAllText(listeningPath) == listeningBefore && WhisperEngine.Shared.ProcessId == 0, "Settings listening or engine state changed");
        }
        void Check(string name, Action run) { try { run(); Hidden(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        (Personalization Personal, AppRegistry Apps) Stores(string label)
        {
            var root = Path.Combine(folder, label + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var personal = new Personalization(Path.Combine(root, "personal.json")); personal.SaveNote(null, "기존 기억", 0);
            var apps = new AppRegistry(Path.Combine(root, "apps.json")); apps.Save("현지 노트", binary); return (personal, apps);
        }
        PersonalImportSource Source()
        {
            var id = "custom:" + Guid.NewGuid().ToString("N"); var state = new PersonalState { Notes = new() { "가져온 기억 (2026-10-29)" }, Routines = new() { ["노트 작업"] = new() { new("open_app", id), new("volume", Number: 30) } } };
            return new(PersonalDataTransfer.Create(state, new[] { new RegisteredApp(id, "Mac 작업 노트", "/Applications/Notes.app") }), new[] { "Mac 원본의 앱을 이 PC에서 사용할 앱에 연결하세요." });
        }
        void Render(Window view, string name, int width = 900, int height = 790)
        {
            var content = (FrameworkElement)view.Content; view.Content = null;
            var canvas = new Grid { Background = (Brush)Application.Current.FindResource("CanvasBrush") }; canvas.Children.Add(content);
            canvas.Measure(new Size(width, height)); canvas.Arrange(new Rect(0, 0, width, height)); canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var target = Path.Combine(AppPaths.WindowsRoot, "artifacts", "personal-data-0250", name); Directory.CreateDirectory(Path.GetDirectoryName(target)!); using var output = File.Create(target); encoder.Save(output);
            Require(new FileInfo(target).Length > 5000, "Empty offscreen render");
        }
        try
        {
            Check("Hidden settings and personal manager expose data import export and persistent restore actions", () =>
            {
                var main = Track(new MainWindow());
                foreach (var id in new[] { "ExportAppsButton", "ImportAppsButton", "ImportMacAppsButton", "RestoreAppsButton" }) Require(main.FindName(id) is Button, "Missing data action: " + id);
                var store = Stores("manager"); var manager = Track(new PersonalWindow(store.Personal));
                var panel = (DockPanel)manager.Content; var header = panel.Children.OfType<StackPanel>().First(); var labels = header.Children.OfType<WrapPanel>().SelectMany(x => x.Children.OfType<Button>()).Select(x => x.Content?.ToString()).ToArray();
                Require(labels.Contains("개인 데이터 내보내기") && labels.Contains("Mac 기억·루틴 가져오기") && labels.Contains("이전 저장 복원"), "Personal transfer toolbar missing");
            });
            Check("Cancelling hidden personal review changes neither data backup nor source and freezes its input", () =>
            {
                var stores = Stores("cancel"); var source = Source(); var original = File.ReadAllText(stores.Personal.StoragePath); var review = Track(new PersonalImportWindow(source, stores.Personal, stores.Apps));
                source.Package.Personal.Notes[0] = "mutated after opening"; source.Notices[0] = "mutated notice";
                Require(review.Description.Contains("가져온 기억") && !review.Description.Contains("mutated") && !review.Apply.IsEnabled && review.Draft.Blocked.Length == 1, "Review is live linked or enabled an unbound app");
                review.Close(); Require(File.ReadAllText(stores.Personal.StoragePath) == original && !File.Exists(stores.Personal.BackupPath), "Cancellation wrote state");
            });
            Check("Actual hidden app selector enables personal apply and the real store saves backup and target ID", () =>
            {
                var stores = Stores("apply"); var source = Source(); var review = Track(new PersonalImportWindow(source, stores.Personal, stores.Apps)); var original = File.ReadAllText(stores.Personal.StoragePath);
                review.MapApp(source.Package.Apps.Single().Id, stores.Apps.Items.Single().Id);
                Require(review.Apply.IsEnabled && review.Description.Contains("현지 노트") && !review.Description.Contains("open_app:"), "App binding or readable review failed"); review.CommitSelection();
                Require(review.Applied && new Personalization(stores.Personal.StoragePath).Snapshot().Routines["노트 작업"][0].Value == stores.Apps.Items.Single().Id && File.ReadAllText(stores.Personal.BackupPath) == original, "Actual apply lost binding backup or persistence"); Reject(review.CommitSelection);
            });
            Check("Hidden collection selection imports only notes without unresolved routine bindings", () =>
            {
                var stores = Stores("notes"); var review = Track(new PersonalImportWindow(Source(), stores.Personal, stores.Apps)); review.SelectOptions(new(Routines: false, Learned: false));
                Require(review.Apply.IsEnabled && review.Draft.Blocked.Length == 0, "Unselected routine still blocked notes"); review.CommitSelection();
                Require(stores.Personal.Snapshot().Notes.Count == 2 && stores.Personal.Snapshot().Routines.Count == 0, "Unselected actions imported");
            });
            Check("Personal review rejects a newer saved note or app binding without partial writes", () =>
            {
                var stores = Stores("stale"); var source = Source(); var review = Track(new PersonalImportWindow(source, stores.Personal, stores.Apps)); review.MapApp(source.Package.Apps.Single().Id, stores.Apps.Items.Single().Id);
                stores.Personal.SaveNote(null, "newer", stores.Personal.Version); var current = File.ReadAllText(stores.Personal.StoragePath); Reject(review.CommitSelection); Require(File.ReadAllText(stores.Personal.StoragePath) == current && !review.Applied, "Stale review overwrote a note");
                var binding = Track(new PersonalImportWindow(source, stores.Personal, stores.Apps)); binding.MapApp(source.Package.Apps.Single().Id, stores.Apps.Items.Single().Id); stores.Apps.Save("추가 앱", binary);
                Reject(binding.CommitSelection); Require(File.ReadAllText(stores.Personal.StoragePath) == current, "Changed app registry allowed a stale binding");
            });
            Check("Hidden app row editing relinks a foreign path and persists without starting a program", () =>
            {
                var stores = Stores("app-edit"); var incoming = new AppImportRow { Name = "이전 노트", Path = "/Applications/Notes.app", Hint = "Mac Notes" }; var review = Track(new AppImportWindow(new[] { incoming }, stores.Apps));
                incoming.Name = "mutated external input"; Require(!review.Apply.IsEnabled && review.Description.Contains("이전 노트"), "Foreign path enabled apply or input was aliased");
                review.SetRow(0, "옮긴 노트", binary); Require(review.Apply.IsEnabled && review.Description.Contains("옮긴 노트"), "Actual row changes did not update preview"); var previewId = review.Draft.Next.Single(x => x.Name == "옮긴 노트").Id; review.CommitSelection();
                Require(new AppRegistry(stores.Apps.StoragePath).Find(previewId)?.Path == binary && AppDataTransfer.ReadBackup(stores.Apps.BackupPath).Length == 1, "App ID path or backup changed at commit");
            });
            Check("Hidden app conflict controls keep existing targets until explicit replacement", () =>
            {
                var stores = Stores("conflict"); var original = stores.Apps.Items.Single(); var other = Path.Combine(Environment.SystemDirectory, "calc.exe"); var review = Track(new AppImportWindow(new[] { new AppImportRow { Name = original.Name, Path = other } }, stores.Apps));
                Require(!review.Apply.IsEnabled && review.Description.Contains("기존 앱 유지"), "Conflict default overwrote a path"); review.ReplaceConflicts(true); Require(review.Apply.IsEnabled, "Explicit replacement unavailable"); review.CommitSelection();
                Require(stores.Apps.Items.Single().Id == original.Id && stores.Apps.Items.Single().Path == other, "Replacement invalidated saved routine references");
            });
            Check("Hidden actual apply handles a failed file replacement and permits a later successful retry", () =>
            {
                var stores = Stores("locked"); stores.Personal.SaveNote(null, "second", stores.Personal.Version); var review = Track(new PersonalImportWindow(Source(), stores.Personal, stores.Apps)); review.SelectOptions(new(Routines: false, Learned: false));
                var before = File.ReadAllText(stores.Personal.StoragePath); var backup = File.ReadAllText(stores.Personal.BackupPath);
                using (File.Open(stores.Personal.BackupPath, FileMode.Open, FileAccess.Read, FileShare.None)) review.Apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!review.Applied && File.ReadAllText(stores.Personal.StoragePath) == before && File.ReadAllText(stores.Personal.BackupPath) == backup, "Failed UI apply changed accepted files");
                review.CommitSelection(); Require(review.Applied && stores.Personal.Snapshot().Notes.Count == 3, "Failed apply could not be retried safely");
            });
            Check("Persistent personal and registered app backups restore through hidden review controls", () =>
            {
                var stores = Stores("restore"); stores.Personal.SaveNote(null, "second", stores.Personal.Version); stores.Apps.Save("second", binary);
                var personalBackup = PersonalDataTransfer.ReadBackup(stores.Personal.BackupPath); var version = stores.Personal.Version; var stamp = stores.Personal.StorageStamp;
                var personalReview = Track(new DataRestoreWindow("개인 데이터 복원", "검토한 이전 기억", () => stores.Personal.ApplySnapshot(personalBackup, version, stamp))); personalReview.CommitSelection();
                var appBackup = AppDataTransfer.ReadBackup(stores.Apps.BackupPath); var appVersion = stores.Apps.Version; var appStamp = stores.Apps.StorageStamp;
                var appReview = Track(new DataRestoreWindow("등록 앱 복원", "검토한 이전 목록", () => stores.Apps.ApplySnapshot(appBackup, appVersion, appStamp))); appReview.CommitSelection();
                Require(new Personalization(stores.Personal.StoragePath).Snapshot().Notes.Single() == "기존 기억" && new AppRegistry(stores.Apps.StoragePath).Items.Count == 1, "Persistent review restore failed");
            });
            Check("Personal Mac and app transfer views render offscreen with no picker microphone or visible window", () =>
            {
                var stores = Stores("render"); var source = MacPersonalTransfer.Parse(new Dictionary<string, string> { ["memory.md"] = "- 아침에는 녹차를 마십니다 (2026-10-29)\n", ["routines.json"] = "{\"집중 작업\":[\"Vrew 열어\",\"볼륨 30\"],\"다시 확인할 항목\":[\"type hello\"]}" });
                var personal = Track(new PersonalImportWindow(source, stores.Personal, stores.Apps)); Render(personal, "personal-import.png");
                var apps = Track(new AppImportWindow(new[] { new AppImportRow { Name = "작업 노트", Hint = "Mac의 Notes", Path = "" }, new AppImportRow { Name = "그림 도구", Hint = "C:\\old-pc\\paint.exe", Path = "C:\\old-pc\\paint.exe" } }, stores.Apps)); Render(apps, "app-import.png");
                var manager = Track(new PersonalWindow(stores.Personal)); Render(manager, "personal-manager.png", 850, 790);
            });
        }
        finally { foreach (var view in windows) view.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "personal-data-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, visibleWindows = 0, pickerOpened = false, microphoneCapture = false, speakerPlayback = false, realAppExecution = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
