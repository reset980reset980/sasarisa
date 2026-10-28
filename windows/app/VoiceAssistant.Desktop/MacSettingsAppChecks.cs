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

internal static class MacSettingsAppChecks
{
    // Construct and render controls offscreen; never show a picker, window or overlay.
    internal static int Run()
    {
        if (Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); int failed = 0;
        var current = MacSettingsChecks.Current() with { Speak = false }; var view = new MainWindow();
        var reviews = new List<SettingsImportWindow>();
        void Require(bool ok, string text) { if (!ok) throw new Exception(text); }
        void Hidden()
        {
            Require(!view.IsVisible && PresentationSource.FromVisual(view) == null, "Main window was shown");
            foreach (var review in reviews) Require(!review.IsVisible && PresentationSource.FromVisual(review) == null, "Review window was shown");
        }
        void Check(string name, Action action) { try { action(); Hidden(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        const string source = "{\"assistantName\":\"코코\",\"language\":\"ko\",\"brainProvider\":\"ollama\",\"brainModel\":\"local-model\",\"brainBaseURL\":\"http://127.0.0.1:11435\",\"inputDevice\":\"mac-device\",\"outputDevice\":\"mac-output\",\"freeMode\":true,\"keepAudio\":true}";
        var sourcePath = Path.Combine(AppPaths.Home, "mac-config.json"); File.WriteAllText(sourcePath, source); SettingsStore.Save(current); view.LoadSettings(current);
        SettingsImportWindow Review()
        {
            var imported = MacSettingsTransfer.Read(sourcePath, current); var review = new SettingsImportWindow(imported.Settings, current, imported.Notices, false); reviews.Add(review); return review;
        }
        try
        {
            Check("Hidden Windows settings include the Mac import action and cancellation changes no saved data", () =>
            {
                Require(view.FindName("ImportMacSettingsButton") is Button { Content: "Mac 설정 가져오기" }, "Mac import action missing");
                var before = File.ReadAllText(AppPaths.ConfigFile); var review = Review();
                Require(review.Result == null && review.ChangeDescription.Contains("macOS 설정 변환 안내") && review.ChangeDescription.Contains("호출 이름"), "Review skipped conversion or diff");
                review.Close(); Require(File.ReadAllText(AppPaths.ConfigFile) == before && File.ReadAllText(sourcePath) == source, "Cancelled review changed a file");
            });
            Check("Hidden Mac review requires connection selection and always preserves Windows device choices", () =>
            {
                var imported = MacSettingsTransfer.Parse(source, current); var review = new SettingsImportWindow(imported.Settings, current, imported.Notices, false); reviews.Add(review);
                imported.Settings.Name = "changed after review"; review.SelectDevices(true);
                Require(!review.AllowsDeviceImport && !review.ChangeDescription.Contains("AI 제공자"), "Connection/device selection bypassed");
                review.SelectConnections(true); Require(review.ChangeDescription.Contains("AI 제공자") && review.ChangeDescription.Contains("자유 모드"), "Connection diff or exclusion hidden");
                var selected = review.AcceptSelection(); Require(selected.Name == "코코" && selected.BrainProvider == "ollama" && selected.Device == current.Device && selected.OutputId == current.OutputId && selected.Desktop.StartupListening == "off", "Review snapshot or local choices changed");
            });
            Check("Accepted hidden Mac settings persist through the real settings store with the prior profile backup", () =>
            {
                var review = Review(); var selected = review.AcceptSelection(); SettingsStore.Save(selected); view.LoadSettings(selected);
                Require(SettingsStore.Load().Name == "코코" && ((TextBox)view.FindName("NameInput")).Text == "코코", "Applied settings did not reload");
                var prior = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(AppPaths.ConfigFile + ".backup"), SettingsStore.Json)!;
                Require(prior.Name == current.Name && prior.Device == current.Device && File.ReadAllText(sourcePath) == source, "Backup or source not preserved");
            });
            Check("Failed persistence after Mac conversion keeps the last accepted profile and backup without a dialog", () =>
            {
                var before = File.ReadAllText(AppPaths.ConfigFile); var backup = File.ReadAllText(AppPaths.ConfigFile + ".backup"); bool blocked = false;
                using (File.Open(AppPaths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None))
                { try { SettingsStore.Save(MacSettingsTransfer.Parse("{\"assistantName\":\"실패\"}", current).Settings); } catch (IOException) { blocked = true; } }
                Require(blocked && File.ReadAllText(AppPaths.ConfigFile) == before && File.ReadAllText(AppPaths.ConfigFile + ".backup") == backup && !Directory.EnumerateFiles(AppPaths.Home, "config.json.*.tmp").Any(), "Failed write changed accepted data");
            });
            Check("Mac conversion review renders to a PNG without creating a visible window", () =>
            {
                var review = Review(); var content = (FrameworkElement)review.Content; review.Content = null;
                var canvas = new Grid { Background = (Brush)Application.Current.FindResource("CanvasBrush") }; canvas.Children.Add(content);
                canvas.Measure(new Size(750, 690)); canvas.Arrange(new Rect(0, 0, 750, 690)); canvas.UpdateLayout();
                var bitmap = new RenderTargetBitmap(750, 690, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "mac-settings-review.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using (var output = File.Create(path)) encoder.Save(output);
                Require(new FileInfo(path).Length > 5000, "Offscreen rendering empty");
            });
        }
        finally { foreach (var review in reviews) review.Close(); view.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "mac-settings-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, visibleWindows = 0, pickerOpened = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
