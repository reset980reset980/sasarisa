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

internal static class WakePhraseAppChecks
{
    internal static int Run()
    {
        if (Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); int failed = 0, saves = 0;
        var current = new UiSettings { Speak = false, Desktop = new() { SetupComplete = true, StartupListening = "off" } };
        SettingsStore.Save(current); var view = new MainWindow(); view.LoadSettings(current);
        var editor = (TextBox)view.FindName("WakeWordsInput");
        view.SettingsSaved += next => { SettingsStore.Save(next); current = next; view.LoadSettings(next); saves++; return null; };
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        void Check(string name, Action action)
        {
            try { action(); Require(!view.IsVisible && PresentationSource.FromVisual(view) == null, "Settings window became visible"); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
        }
        try
        {
            Check("Hidden WPF editor applies explicit phrases only on Save and backs up the previous profile", () =>
            {
                var before = File.ReadAllText(AppPaths.ConfigFile); editor.Text = " 코코\r\n도와줘\r\n코코";
                Require(File.ReadAllText(AppPaths.ConfigFile) == before && saves == 0, "Typing applied pending settings");
                view.SaveSettingsForCheck(); var stored = SettingsStore.Load();
                Require(saves == 1 && stored.WakeWords.SequenceEqual(new[] { "코코", "도와줘" }) && editor.Text.Contains("도와줘"), "Actual save/reload lost phrases");
                Require(File.ReadAllText(AppPaths.ConfigFile + ".backup") == before && stored.Desktop.StartupListening == "off", "Save failed to back up or changed listening preference");
            });
            Check("Invalid hidden WPF wake edits preserve settings and backup without a dialog", () =>
            {
                var before = File.ReadAllText(AppPaths.ConfigFile); var backup = File.ReadAllText(AppPaths.ConfigFile + ".backup");
                foreach (var text in new[] { new string('가', 41), "!", "코코\u0001", string.Join("\n", Enumerable.Range(0, 33).Select(i => "호출" + i)) })
                {
                    editor.Text = text; view.SaveSettingsForCheck();
                    Require(saves == 1 && File.ReadAllText(AppPaths.ConfigFile) == before && File.ReadAllText(AppPaths.ConfigFile + ".backup") == backup, "Invalid pending settings persisted");
                }
            });
            Check("Clearing the actual editor restores name activation and does not erase learned pronunciations", () =>
            {
                current.LearnedWakes[current.Name] = ["자빗스"]; view.LoadSettings(current); editor.Text = ""; view.SaveSettingsForCheck();
                var stored = SettingsStore.Load(); Require(stored.WakeWords.Length == 0 && LearnedNames.Strip("자빗스 몇 시야", stored) == "몇 시야", "Clearing did not restore learned activation");
            });
            Check("Hidden Mac review exposes explicit wake changes and requires an accepted selection", () =>
            {
                var imported = MacSettingsTransfer.Parse("{\"wakeWords\":[\"음\",\"도와줘\"]}", current);
                var review = new SettingsImportWindow(imported.Settings, current, imported.Notices, false);
                try
                {
                    Require(review.Result == null && review.ChangeDescription.Contains("별도 호출어") && review.ChangeDescription.Contains("도와줘"), "Review hid activation change or pre-applied settings");
                    Require(review.AcceptSelection().WakeWords.SequenceEqual(new[] { "음", "도와줘" }) && SettingsStore.Load().WakeWords.Length == 0, "Review selection changed before actual save");
                    Require(!review.IsVisible && PresentationSource.FromVisual(review) == null, "Mac review was shown");
                }
                finally { review.Close(); }
            });
            Check("The actual wake editor and guidance render to PNG without showing a window", () =>
            {
                editor.Text = "헤이 자비스\n도와줘";
                var section = (StackPanel)view.FindName("WakeWordsSection"); ((Panel)section.Parent).Children.Remove(section);
                var canvas = new Grid { Background = Brushes.White, Margin = new Thickness(20) }; canvas.Children.Add(section);
                canvas.Measure(new Size(600, 260)); canvas.Arrange(new Rect(0, 0, 600, 260)); canvas.UpdateLayout();
                Require(section.ActualHeight <= 260 && editor.ActualHeight >= 76, "Editor clipped offscreen");
                var bitmap = new RenderTargetBitmap(600, 260, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "wake-phrase-editor.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using (var output = File.Create(path)) encoder.Save(output);
                Require(new FileInfo(path).Length > 5000, "Offscreen render empty");
            });
        }
        finally { view.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "wake-phrase-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, visibleWindows = 0, microphoneCapture = false, speakerPlayback = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
