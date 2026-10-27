using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    void LoadDesktopPreferences(DesktopPreferences preferences)
    {
        WakeHotkeyInput.Text = HotkeySpec.Parse(preferences.WakeHotkey)?.Label ?? "사용 안 함";
        StopHotkeyInput.Text = HotkeySpec.Parse(preferences.StopHotkey)?.Label ?? "사용 안 함";
        UserNameInput.Text = preferences.UserName;
        SelectTag(StartupListeningInput, preferences.StartupListening);
        SelectTag(OverlayPositionInput, preferences.OverlayPosition);
        var screens = new List<DeviceOption> { new("active", "현재 사용 중인 화면"), new("primary", "기본 화면") };
        screens.AddRange(System.Windows.Forms.Screen.AllScreens.Select((screen, i) => new DeviceOption(screen.DeviceName, $"화면 {i + 1} · {screen.Bounds.Width} × {screen.Bounds.Height}" + (screen.Primary ? " · 기본" : ""))));
        if (screens.All(x => x.Id != preferences.OverlayScreen)) screens.Add(new(preferences.OverlayScreen, "이전 화면 · 현재 연결 안 됨"));
        OverlayScreenInput.ItemsSource = screens; OverlayScreenInput.SelectedValue = preferences.OverlayScreen;
        IdleOrbInput.IsChecked = preferences.ShowIdleOrb;
        SelectTag(GreetingStyleInput, preferences.GreetingStyle);
        GreetingPhrasesInput.Text = string.Join(Environment.NewLine, preferences.GreetingPhrases);
        GreetingTextInput.Text = preferences.GreetingText; GreetingTitleInput.Text = preferences.GreetingTitle; GreetingSubtitleInput.Text = preferences.GreetingSubtitle;
        GreetingUrlsInput.Text = string.Join(Environment.NewLine, preferences.GreetingUrls);
        RefreshGreetingApps(preferences.GreetingApps);
        HotkeyFeedback.Text = "다른 앱과 겹치는 조합은 저장되지 않습니다. 누르는 동안 말하기는 선택한 말하기 키를 놓을 때 끝납니다.";
    }
    void RefreshGreetingApps(string[]? selected = null)
    {
        selected ??= GreetingAppsInput.SelectedItems.OfType<DeviceOption>().Select(x => x.Id).ToArray();
        var apps = CommandParser.Apps.GroupBy(x => x.Value).Select(x => new DeviceOption(x.Key, x.First().Key)).Concat(AppRegistry.Default.Items.Select(x => new DeviceOption(x.Id, x.Name))).ToList();
        foreach (var id in selected.Where(id => apps.All(x => x.Id != id))) apps.Add(new(id, "이전 앱 · 현재 등록 안 됨"));
        GreetingAppsInput.ItemsSource = apps;
        foreach (var app in apps.Where(x => selected.Contains(x.Id))) GreetingAppsInput.SelectedItems.Add(app);
    }
    public bool CaptureRegisteredHotkey(HotkeySpec? spec)
    {
        if (spec == null || !IsActive || Keyboard.FocusedElement is not TextBox field || (field != WakeHotkeyInput && field != StopHotkeyInput)) return false;
        field.Text = spec.Label; HotkeyFeedback.Text = "저장하면 이 조합이 적용됩니다."; return true;
    }
    static string[] Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
    DesktopPreferences ReadDesktopPreferences() => new()
    {
        UserName = UserNameInput.Text.Trim(), WakeHotkey = HotkeySpec.Parse(WakeHotkeyInput.Text)?.Label ?? "", StopHotkey = HotkeySpec.Parse(StopHotkeyInput.Text)?.Label ?? "",
        OverlayPosition = SelectedTag(OverlayPositionInput), OverlayScreen = (OverlayScreenInput.SelectedItem as DeviceOption)?.Id ?? "active", ShowIdleOrb = IdleOrbInput.IsChecked == true,
        GreetingStyle = SelectedTag(GreetingStyleInput), GreetingText = GreetingTextInput.Text.Trim(), GreetingTitle = GreetingTitleInput.Text.Trim(), GreetingSubtitle = GreetingSubtitleInput.Text.Trim(),
        GreetingPhrases = Lines(GreetingPhrasesInput.Text), GreetingUrls = Lines(GreetingUrlsInput.Text), GreetingApps = GreetingAppsInput.SelectedItems.OfType<DeviceOption>().Select(x => x.Id).ToArray(),
        SetupComplete = _loadedSettings.Desktop.SetupComplete, StartupListening = SelectedTag(StartupListeningInput)
    };
    void HotkeyInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        try
        {
            var modifiers = Keyboard.Modifiers; var prefix = new List<string>();
            if (modifiers.HasFlag(ModifierKeys.Control)) prefix.Add("Ctrl"); if (modifiers.HasFlag(ModifierKeys.Alt)) prefix.Add("Alt"); if (modifiers.HasFlag(ModifierKeys.Shift)) prefix.Add("Shift"); if (modifiers.HasFlag(ModifierKeys.Windows)) prefix.Add("Win");
            var name = key switch { Key.Back => "Backspace", Key.Return => "Enter", Key.Prior => "PageUp", Key.Next => "PageDown", >= Key.D0 and <= Key.D9 => ((char)('0' + key - Key.D0)).ToString(), _ => key.ToString() };
            prefix.Add(name); ((TextBox)sender).Text = HotkeySpec.Parse(string.Join('+', prefix))!.Label;
            HotkeyFeedback.Text = "저장하면 이 조합이 적용됩니다.";
        }
        catch (ArgumentException ex) { HotkeyFeedback.Text = ex.Message; }
    }
    void DisableWakeHotkey_Click(object sender, RoutedEventArgs e) => WakeHotkeyInput.Text = "사용 안 함";
    void ResetHotkeys_Click(object sender, RoutedEventArgs e) { WakeHotkeyInput.Text = "Ctrl+Shift+Space"; StopHotkeyInput.Text = "Ctrl+Alt+Backspace"; }
    void PreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        try { var preferences = ReadDesktopPreferences(); preferences.Validate(SelectedTag(ActivationInput)); OverlayPreviewRequested?.Invoke(preferences); }
        catch (ArgumentException ex) { SettingsError(ex.Message, OverlayPositionInput); }
    }
    void PreviewGreeting_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var preferences = ReadDesktopPreferences(); preferences.Validate(SelectedTag(ActivationInput));
            var speech = ReadSpeechOptions(); if (speech == null) return;
            GreetingPreviewRequested?.Invoke(_loadedSettings with { Name = string.IsNullOrWhiteSpace(NameInput.Text) ? "자비스" : NameInput.Text.Trim(), Desktop = preferences, VoiceId = speech.VoiceId, OutputId = speech.OutputId, SpeechRate = speech.SpeechRate, SpeechVolume = speech.SpeechVolume, Tts = speech.Tts, BargeIn = speech.BargeIn });
        }
        catch (ArgumentException ex) { SettingsError(ex.Message, GreetingTextInput); }
    }
    void Setup_Click(object sender, RoutedEventArgs e) => SetupRequested?.Invoke();
}
