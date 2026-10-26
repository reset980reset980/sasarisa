using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    void RefreshRegisteredApps(string? selectedId = null)
    {
        RegisteredAppsInput.ItemsSource = AppRegistry.Default.Items;
        RegisteredAppsInput.SelectedValue = selectedId;
        RefreshGreetingApps();
        AppRegistrationFeedback.Text = AppRegistry.Default.Error ?? "등록한 이름에 ‘열어’를 붙여 말하세요. 예: ‘작업 노트 열어’.";
    }
    void RegisteredAppsInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var app = RegisteredAppsInput.SelectedItem as RegisteredApp;
        AppNameInput.Text = app?.Name ?? ""; AppPathInput.Text = app?.Path ?? "";
        RemoveAppButton.IsEnabled = app != null;
    }
    void NewRegisteredApp_Click(object sender, RoutedEventArgs e)
    {
        RegisteredAppsInput.SelectedItem = null; AppNameInput.Clear(); AppPathInput.Clear(); AppNameInput.Focus();
    }
    void ChooseRegisteredApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "등록할 앱의 실행 파일 또는 바로가기 선택", Filter = "앱과 바로가기|*.exe;*.lnk", CheckFileExists = true, Multiselect = false, DereferenceLinks = false };
        if (dialog.ShowDialog(this) == true) AppPathInput.Text = dialog.FileName;
    }
    void SaveRegisteredApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = AppRegistry.Default.Save(AppNameInput.Text, AppPathInput.Text, (RegisteredAppsInput.SelectedItem as RegisteredApp)?.Id);
            RefreshRegisteredApps(app.Id); AppRegistrationFeedback.Text = $"‘{app.Name}’ 앱을 등록했어요. 바로 말하거나 입력해서 실행할 수 있어요."; AppRegistrationFeedback.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { AppRegistrationFeedback.Text = ex.Message; AppRegistrationFeedback.Foreground = Brushes.Firebrick; }
    }
    void RemoveRegisteredApp_Click(object sender, RoutedEventArgs e)
    {
        if (RegisteredAppsInput.SelectedItem is not RegisteredApp app) return;
        try
        {
            AppRegistry.Default.Remove(app.Id); RefreshRegisteredApps(); AppRegistrationFeedback.Text = "목록에서 제거했어요. 앱 파일은 그대로 있어요."; AppRegistrationFeedback.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { AppRegistrationFeedback.Text = ex.Message; AppRegistrationFeedback.Foreground = Brushes.Firebrick; }
    }
}
