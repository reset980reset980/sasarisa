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

internal static class CodexEffortAppChecks
{
    internal static int Run()
    {
        if (Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); int failed = 0, saves = 0;
        var current = new UiSettings { BrainProvider = "codex", Speak = false, Desktop = new() { SetupComplete = true, StartupListening = "off" } };
        SettingsStore.Save(current); var view = new MainWindow(); view.LoadSettings(current);
        var choice = (ComboBox)view.FindName("CodexEffortInput"); var provider = (ComboBox)view.FindName("ProviderInput"); var section = (StackPanel)view.FindName("CodexEffortFields");
        view.SettingsSaved += next => { SettingsStore.Save(next); current = next; view.LoadSettings(next); saves++; return null; };
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        void Provider(string id) { provider.SelectedItem = provider.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == id); }
        void Check(string name, Action action)
        {
            try { action(); Require(!view.IsVisible && PresentationSource.FromVisual(view) == null, "Main window became visible"); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
        }
        try
        {
            Check("Actual hidden Codex effort editor saves each level only on Apply and keeps the previous backup", () =>
            {
                Require(Tag(choice) == "low" && section.Visibility == Visibility.Visible, "Legacy default or Codex choice hidden");
                foreach (var value in new[] { "low", "medium", "high" })
                {
                    var before = File.ReadAllText(AppPaths.ConfigFile); choice.SelectedItem = choice.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == value);
                    Require(File.ReadAllText(AppPaths.ConfigFile) == before, "Pending choice applied before Save");
                    view.SaveSettingsForCheck();
                    Require(SettingsStore.Load().BrainEffort == value && Tag(choice) == value && File.ReadAllText(AppPaths.ConfigFile + ".backup") == before, "Actual save/reload/backup lost thinking");
                }
                Require(saves == 3 && current.Desktop.StartupListening == "off", "Thinking changed listening preference");
            });
            Check("Provider switches retain the draft thinking choice and show it only for Codex or Combo", () =>
            {
                Provider("claude"); Require(section.Visibility == Visibility.Collapsed && Tag(choice) == "high", "Claude switch erased choice");
                view.SaveSettingsForCheck(); Require(SettingsStore.Load().BrainEffort == "high", "Saving another provider reset Codex thinking");
                Provider("combo"); Require(section.Visibility == Visibility.Visible && Tag(choice) == "high", "Combo omitted Codex setting");
                Provider("codex"); Require(section.Visibility == Visibility.Visible && Tag(choice) == "high", "Codex switch lost draft");
            });
            Check("Invalid hidden Codex selection preserves the profile and backup without an error dialog", () =>
            {
                var before = File.ReadAllText(AppPaths.ConfigFile); var backup = File.ReadAllText(AppPaths.ConfigFile + ".backup"); var count = saves;
                choice.SelectedIndex = -1; view.SaveSettingsForCheck();
                Require(saves == count && File.ReadAllText(AppPaths.ConfigFile) == before && File.ReadAllText(AppPaths.ConfigFile + ".backup") == backup, "Invalid selection persisted");
                view.LoadSettings(current);
            });
            Check("Actual hidden Mac import review preserves thinking until AI connection import is selected", () =>
            {
                var imported = MacSettingsTransfer.Parse("{\"brainEffort\":\"medium\"}", current); var review = new SettingsImportWindow(imported.Settings, current, imported.Notices, false);
                try
                {
                    Require(review.Result == null && !review.ChangeDescription.Contains("Codex 생각 깊이"), "Thinking changed without opt-in");
                    review.SelectConnections(true); Require(review.ChangeDescription.Contains("Codex 생각 깊이") && review.ChangeDescription.Contains("보통"), "Connection review omitted thinking");
                    Require(review.AcceptSelection().BrainEffort == "medium" && SettingsStore.Load().BrainEffort == "high", "Review selection prematurely persisted");
                    Require(!review.IsVisible && PresentationSource.FromVisual(review) == null, "Review became visible");
                }
                finally { review.Close(); }
            });
            Check("Actual Codex thinking control renders offscreen with the selected value and guidance", () =>
            {
                view.LoadSettings(current with { BrainProvider = "codex", BrainEffort = "medium" });
                ((Panel)section.Parent).Children.Remove(section); var canvas = new Grid { Background = Brushes.White, Margin = new Thickness(20) }; canvas.Children.Add(section);
                canvas.Measure(new Size(600, 210)); canvas.Arrange(new Rect(0, 0, 600, 210)); canvas.UpdateLayout();
                Require(section.Visibility == Visibility.Visible && Tag(choice) == "medium" && choice.ActualHeight > 20, "Offscreen choice missing");
                var bitmap = new RenderTargetBitmap(600, 210, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "codex-effort-editor.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using (var output = File.Create(path)) encoder.Save(output);
                Require(new FileInfo(path).Length > 5000, "Offscreen PNG empty");
            });
        }
        finally { view.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "codex-effort-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, visibleWindows = 0, microphoneCapture = false, speakerPlayback = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
