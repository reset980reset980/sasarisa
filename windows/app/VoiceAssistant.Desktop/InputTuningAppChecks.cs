using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

public partial class App
{
    int RunInputTuningAppChecks()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); window.SettingsSaved += SaveSettings;
        var rows = new List<object>(); int failed = 0;
        void Require(bool ok, string text) { if (!ok) throw new Exception(text); }
        void Check(string name, Action action) { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        try
        {
            Check("Input tuning legacy defaults and export/import preserve explicit choices", () =>
            {
                var legacy = JsonSerializer.Deserialize<UiSettings>("{}")!; Require(legacy.InputGain == 1 && !legacy.WakeHints, "Legacy recording changed");
                var transferred = SettingsTransfer.Parse(SettingsTransfer.Serialize(settings with { InputGain = 4, WakeHints = true }));
                Require(transferred.InputGain == 4 && transferred.WakeHints, "Tuning lost in transfer");
                foreach (var gain in new[] { .24, 20.01, double.NaN, double.PositiveInfinity })
                { try { SettingsStore.Validate(settings with { InputGain = gain }); throw new Exception("Invalid gain accepted"); } catch (ArgumentException) { } }
            });
            Check("Actual WPF settings save and reload gain and wake hints without starting capture", () =>
            {
                foreach (var gain in new[] { .25, 1, 5, 20 })
                {
                    window.InputGainInput.Text = gain.ToString(System.Globalization.CultureInfo.InvariantCulture); window.WakeHintsInput.IsChecked = gain > 1;
                    window.SaveSettingsForCheck(); var saved = SettingsStore.Load();
                    Require(saved.InputGain == gain && saved.WakeHints == (gain > 1) && settings.InputGain == gain, "Actual handler lost tuning");
                    Require(!listening && !starting && !input.Running, "Settings unexpectedly started microphone");
                }
            });
            Check("Invalid WPF gain preserves the last saved configuration and pending checkbox", () =>
            {
                var before = File.ReadAllBytes(AppPaths.ConfigFile);
                foreach (var value in new[] { "NaN", "Infinity", "0", "21", "invalid" })
                {
                    window.InputGainInput.Text = value; window.WakeHintsInput.IsChecked = false; window.SaveSettingsForCheck();
                    Require(Convert.ToHexString(before) == Convert.ToHexString(File.ReadAllBytes(AppPaths.ConfigFile)) && settings.InputGain == 20 && settings.WakeHints, "Invalid save changed settings");
                }
            });
        }
        finally { StopInput(); StopTurn(); overlay.Close(); window.Close(); }
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", "input-tuning-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
