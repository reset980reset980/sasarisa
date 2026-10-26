using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    public event Action<Action>? SettingsFileRequested;
    void ExportSettings_Click(object sender, RoutedEventArgs e) => SettingsFileRequested?.Invoke(() =>
    {
        try
        {
            var dialog = new SaveFileDialog { Title = "저장된 설정 내보내기", Filter = "Windows 비서 설정 (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = "자비스-설정-" + DateTime.Now.ToString("yyyyMMdd") + ".json", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            SettingsTransfer.Write(dialog.FileName, _loadedSettings); SettingsFeedback.Text = "저장된 설정을 내보냈어요. API 키·로그인·기억·루틴·등록 앱은 포함하지 않습니다."; SettingsFeedback.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { SettingsFeedback.Text = "내보내지 못했어요. " + ex.Message; SettingsFeedback.Foreground = Brushes.Firebrick; }
    });
    void ImportSettings_Click(object sender, RoutedEventArgs e) => SettingsFileRequested?.Invoke(() =>
    {
        try
        {
            var dialog = new OpenFileDialog { Title = "Windows 비서 설정 가져오기", Filter = "Windows 비서 설정 (*.json)|*.json", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            var imported = SettingsTransfer.Read(dialog.FileName);
            var review = new SettingsImportWindow(imported, _loadedSettings) { Owner = this };
            if (review.ShowDialog() != true || review.Result == null) { SettingsFeedback.Text = "가져오기를 취소했어요. 현재 설정을 유지합니다."; return; }
            var error = SettingsSaved?.Invoke(review.Result);
            SettingsFeedback.Text = error ?? "설정을 가져왔어요. 이전 설정은 config.json.backup에 보관했습니다.";
            SettingsFeedback.Foreground = error == null ? Brushes.DarkGreen : Brushes.Firebrick;
        }
        catch (Exception ex) { SettingsFeedback.Text = "가져오지 못했어요. " + ex.Message; SettingsFeedback.Foreground = Brushes.Firebrick; }
    });
}

public sealed class SettingsImportWindow : Window
{
    public UiSettings? Result { get; private set; }
    public SettingsImportWindow(UiSettings imported, UiSettings current)
    {
        Title = "가져올 설정 확인"; Width = 750; Height = 720; MinWidth = 600; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("CanvasBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "바뀌는 설정을 확인하세요", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "적용하면 저장된 설정과 화면에서 편집 중인 값을 바꿉니다.\nAPI 키·로그인·기억·루틴·등록 앱은 현재 상태를 유지합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,14), Foreground = Brushes.SlateGray });
        var connections = new CheckBox { Content = "AI 연결·Whisper 주소·코딩 폴더도 가져오기", Margin = new Thickness(0,0,0,10) };
        var devices = new CheckBox { Content = "마이크·목소리·출력 장치·알림 화면도 가져오기", Margin = new Thickness(0,0,0,15) }; header.Children.Add(connections); header.Children.Add(devices);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(20,9,20,9), Margin = new Thickness(0,0,10,0), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var apply = new Button { Content = "확인한 설정 적용", Padding = new Thickness(20,9,20,9), Style = (Style)Application.Current.FindResource("ActionButton") }; footer.Children.Add(cancel); footer.Children.Add(apply);
        var changes = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(15), BorderBrush = Brushes.LightGray }; System.Windows.Automation.AutomationProperties.SetName(changes, "가져올 설정 변경 내용"); root.Children.Add(changes);
        void Refresh() { var next = SettingsTransfer.Prepare(imported, current, connections.IsChecked == true, devices.IsChecked == true); changes.Text = SettingsTransfer.Describe(current, next); }
        connections.Checked += (_,_) => Refresh(); connections.Unchecked += (_,_) => Refresh(); devices.Checked += (_,_) => Refresh(); devices.Unchecked += (_,_) => Refresh();
        apply.Click += (_,_) => { Result = SettingsTransfer.Prepare(imported, current, connections.IsChecked == true, devices.IsChecked == true); DialogResult = true; }; Refresh();
    }
}
