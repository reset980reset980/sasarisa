using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceAssistant.Desktop;

internal static class InputDeviceAppChecks
{
    internal static int Run()
    {
        if (Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
        var rows = new List<object>(); int failed = 0, trainingRequests = 0;
        var current = new UiSettings { Device = "1", DeviceLabel = "기존 마이크", Speak = false, Desktop = new() { SetupComplete = true, StartupListening = "off" } };
        SettingsStore.Save(current); var view = new MainWindow(); view.LoadSettings(current);
        var input = (ComboBox)view.FindName("DeviceInput"); var help = (TextBlock)view.FindName("InputDeviceHelp");
        var chosen = new DeviceOption(InputDeviceChecks.Example(), "선택한 마이크 · Windows WASAPI") { LegacyId = "1", IdentityKind = "wasapi" };
        var other = new DeviceOption(InputDeviceChecks.Example("b"), "다른 마이크") { LegacyId = "2", IdentityKind = "wasapi" };
        view.SettingsSaved += next => { SettingsStore.Save(next); current = next; view.LoadSettings(next); return null; };
        view.NameTrainingRequested += (_, _) => trainingRequests++;
        DeviceOption Selected() => (DeviceOption)input.SelectedItem;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Check(string name, Action action)
        {
            try { action(); Require(!view.IsVisible && PresentationSource.FromVisual(view) == null, "Settings window was shown"); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
        }
        try
        {
            Check("Hidden legacy input selection survives inventory reorder and migrates only when Save is pressed", () =>
            {
                var before = File.ReadAllText(AppPaths.ConfigFile); view.SetDevices(new[] { chosen, other }, current.Device);
                Require(Selected().Id == chosen.Id && File.ReadAllText(AppPaths.ConfigFile) == before, "Reading inventory changed settings");
                view.SetDevices(new[] { other with { LegacyId = "1" }, chosen with { LegacyId = "9" } }, current.Device);
                Require(Selected().Id == chosen.Id, "Index reuse selected a different microphone");
                view.SaveSettingsForCheck();
                Require(current.Device == chosen.Id && current.DeviceLabel == chosen.Label && SettingsStore.Load().Device == chosen.Id && File.ReadAllText(AppPaths.ConfigFile + ".backup") == before, "Actual migration/save/backup failed");
            });
            Check("Missing input remains selected across refresh and unrelated settings saves", () =>
            {
                view.SetDevices(new[] { other with { LegacyId = "1" } }, current.Device);
                Require(Selected().Unavailable && Selected().Id == chosen.Id && help.Text.Contains("유지"), "Unplug silently selected another device");
                typeof(MainWindow).GetMethod("NameTraining_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object[] { view, new RoutedEventArgs() });
                Require(trainingRequests == 0, "Missing device started name training");
                ((TextBox)view.FindName("ThreadsInput")).Text = "3"; view.SaveSettingsForCheck();
                var saved = SettingsStore.Load(); Require(saved.Device == chosen.Id && saved.DeviceLabel == chosen.Label && saved.Threads == 3, "Unrelated save erased the missing device");
                view.SetDevices(new[] { other with { LegacyId = "1" } }, current.Device);
                Require(Selected().Id == chosen.Id && Selected().RememberedLabel == chosen.Label && !Selected().Label.Contains("현재 연결 없음 · 현재 연결 없음"), "Repeated refresh changed or duplicated the remembered label");
            });
            Check("Reconnected input resolves its saved identity despite a new current index", () =>
            {
                view.SetDevices(new[] { other, chosen with { LegacyId = "12" } }, current.Device);
                Require(!Selected().Unavailable && Selected().Id == chosen.Id && Selected().LegacyId == "12" && help.Text.Contains("식별자"), "Reconnected microphone lost identity");
            });
            Check("Pending input choice survives refresh and default fallback requires an explicit user selection", () =>
            {
                input.SelectedItem = input.Items.Cast<DeviceOption>().Single(x => x.Id == other.Id); var before = File.ReadAllText(AppPaths.ConfigFile);
                view.SetDevices(new[] { chosen, other with { LegacyId = "5" } }, current.Device);
                Require(Selected().Id == other.Id && File.ReadAllText(AppPaths.ConfigFile) == before, "Refresh replaced or applied the user's pending choice");
                input.SelectedItem = input.Items.Cast<DeviceOption>().Single(x => x.Id == ""); view.SaveSettingsForCheck();
                Require(SettingsStore.Load().Device == "" && current.Desktop.StartupListening == "off", "Explicit default or listening preference changed");
            });
            Check("Saved input identity and remembered label reload through the real settings store", () =>
            {
                view.SetDevices(new[] { chosen, other }, current.Device); input.SelectedItem = input.Items.Cast<DeviceOption>().Single(x => x.Id == chosen.Id); view.SaveSettingsForCheck();
                var stored = SettingsStore.Load(); view.LoadSettings(stored); view.SetDevices(new[] { chosen with { LegacyId = "7" } }, stored.Device);
                Require(Selected().Id == chosen.Id && stored.DeviceLabel == chosen.Label && Selected().LegacyId == "7", "Reload changed selected input");
            });
            Check("Actual unavailable input control and recovery guidance render without displaying a window", () =>
            {
                view.SetDevices(Array.Empty<DeviceOption>(), current.Device);
                var section = (StackPanel)view.FindName("InputDeviceSection"); ((Panel)section.Parent).Children.Remove(section);
                var canvas = new Grid { Background = Brushes.White, Margin = new Thickness(20) }; canvas.Children.Add(section);
                canvas.Measure(new Size(650, 210)); canvas.Arrange(new Rect(0, 0, 650, 210)); canvas.UpdateLayout();
                Require(Selected().Unavailable && input.ActualWidth > 200 && help.ActualHeight > 10, "Missing input presentation empty");
                var bitmap = new RenderTargetBitmap(650, 210, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "input-device-editor.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using (var output = File.Create(path)) encoder.Save(output);
                Require(new FileInfo(path).Length > 5000, "Input editor render empty");
            });
        }
        finally { view.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "input-device-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, visibleWindows = 0, microphoneCapture = false, speakerPlayback = false, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
