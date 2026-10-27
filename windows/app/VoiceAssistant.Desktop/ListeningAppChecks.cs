using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace VoiceAssistant.Desktop;

public partial class App
{
    int RunListeningAppChecks()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture();
        window.SettingsSaved += SaveSettings;
        var rows = new List<object>(); int failed = 0;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        void Check(string name, Action action)
        { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        try
        {
            Check("Actual WPF settings save and reload all startup listening choices without starting capture", () =>
            {
                foreach (var mode in new[] { "always", "off", "restore" })
                {
                    var combo = (ComboBox)window.FindName("StartupListeningInput");
                    combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(x => (string)x.Tag == mode);
                    window.SaveSettingsForCheck();
                    Require(settings.Desktop.StartupListening == mode && SettingsStore.Load().Desktop.StartupListening == mode, "Choice not saved: " + mode);
                    Require(((ComboBoxItem)combo.SelectedItem).Tag.ToString() == mode && !listening && !starting, "Choice changed or capture started");
                }
            });
            Check("Temporary App input stops and settings changes preserve requested listening", () =>
            {
                RememberListening(true); StopInput();
                Require(SaveSettings(settings with { Speak = false }) == null, "Settings save failed");
                Require(listeningState.Requested && new ListeningState().Requested && !listening && !starting, "Temporary stop changed intent");
            });
            Check("Explicit App listening stop persists across a fresh state load", () =>
            {
                listening = true; ToggleListening();
                Require(!listening && !starting && !listeningState.Requested && !new ListeningState().Requested, "Manual stop was not persisted");
            });
            Check("Actual App startup refuses capture until setup is complete even with explicit launch", () =>
            {
                settings = settings with { Desktop = settings.Desktop with { SetupComplete = false, StartupListening = "always" } };
                RestoreListening(true);
                Require(!listening && !starting && listeningState.Requested, "Readiness gate or explicit request lost");
            });
        }
        finally { StopInput(); StopTurn(); overlay.Close(); window.Close(); }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "listening-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}

public partial class MainWindow
{
    // Exercise the actual save handler in the isolated WPF fixture without showing a window.
    internal void SaveSettingsForCheck() => SaveSettings_Click(this, new RoutedEventArgs());
}
