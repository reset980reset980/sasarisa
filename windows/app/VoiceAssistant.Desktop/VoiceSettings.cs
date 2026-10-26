using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    VoicePreferences voicePreferences = new();
    VoiceSettingsWindow? voiceSettingsWindow;
    void LoadVoicePreferences(VoicePreferences preferences) { voicePreferences = preferences with { }; VoiceProviderSummary.Text = ExternalVoice.Name(preferences.Provider); }
    public void ReportVoicePreview(string text, bool warning) => voiceSettingsWindow?.Report(text, warning);
    void VoiceProviderSettings_Click(object sender, RoutedEventArgs e)
    {
        var speech = ReadSpeechOptions(); if (speech == null) return;
        StopRequested?.Invoke();
        var dialog = new VoiceSettingsWindow(speech) { Owner = this }; voiceSettingsWindow = dialog;
        dialog.PreviewRequested += value => SpeakTestRequested?.Invoke(value);
        dialog.StopRequested += () => StopRequested?.Invoke();
        try { if (dialog.ShowDialog() == true) { voicePreferences = dialog.Result!; VoiceProviderSummary.Text = ExternalVoice.Name(voicePreferences.Provider) + " · 설정 저장 전"; } }
        finally { StopRequested?.Invoke(); voiceSettingsWindow = null; }
    }
}

public sealed class VoiceSettingsWindow : Window
{
    readonly UiSettings original;
    readonly ComboBox provider = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id", MinHeight = 34 };
    readonly ComboBox proVoice = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id", MinHeight = 34 };
    readonly StackPanel proVoiceRow = new();
    readonly TextBox voice = Field("외부 목소리 ID"), model = Field("외부 음성 모델"), region = Field("Azure 음성 지역"), url = Field("외부 음성 서버 주소"), speed = Field("외부 음성 속도");
    readonly PasswordBox key = new() { MinHeight = 34, Padding = new Thickness(8) };
    readonly CheckBox fallback = new() { Content = "외부 음성 실패 시 Windows 기본 목소리로 읽기", Margin = new Thickness(0,14,0,10) };
    readonly TextBlock help = Help(""), destination = Help(""), keyStatus = Help(""), feedback = Help("");
    readonly StackPanel voiceRow = new(), modelRow = new(), regionRow = new(), urlRow = new(), speedRow = new(), keyRow = new();
    readonly Dictionary<string, VoicePreferences> drafts = new();
    string selected = ""; bool loading;
    public VoicePreferences? Result { get; private set; }
    public event Action<UiSettings>? PreviewRequested;
    public event Action? StopRequested;
    static TextBox Field(string name) { var box = new TextBox { MinHeight = 34, Padding = new Thickness(8) }; AutomationProperties.SetName(box, name); return box; }
    static TextBlock Help(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0,7,0,8), LineHeight = 20 };
    static void FieldRow(StackPanel row, string label, UIElement field) { row.Margin = new Thickness(0,8,0,0); row.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,6) }); row.Children.Add(field); }
    static Button Button(string label, RoutedEventHandler clicked) { var button = new Button { Content = label, Padding = new Thickness(14,8,14,8), Margin = new Thickness(0,0,9,0), MinWidth = 75 }; button.Click += clicked; return button; }
    public VoiceSettingsWindow(UiSettings settings)
    {
        original = settings; Title = "목소리 제공자 설정"; Width = 620; Height = 700; MinWidth = 530; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(247,247,241)); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(24) };
        var buttons = new WrapPanel { Margin = new Thickness(0,16,0,0) }; DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(Button("목소리 시험", Preview)); buttons.Children.Add(Button("시험 중지", (_,_) => { StopRequested?.Invoke(); Report("시험 출력을 중지했어요.", false); }));
        buttons.Children.Add(Button("이 설정 사용", Apply)); buttons.Children.Add(Button("닫기", (_,_) => Close())); root.Children.Add(buttons);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
        panel.Children.Add(new TextBlock { Text = "답변을 읽어 줄 목소리", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(Help("외부 제공자에는 읽을 문장이 전달되며 서비스 사용료가 발생할 수 있습니다. 재생되는 음성은 합성된 AI 목소리입니다. 출력 장치와 음량은 앞의 설정을 사용합니다."));
        provider.ItemsSource = new[] { new DeviceOption("windows","Windows 기본 음성"), new DeviceOption("pro","Sasarisa Pro"), new DeviceOption("elevenlabs","ElevenLabs"), new DeviceOption("openai","OpenAI"), new DeviceOption("azure","Azure Speech"), new DeviceOption("http","사용자 음성 서버") };
        AutomationProperties.SetName(provider, "음성 제공자"); panel.Children.Add(provider); panel.Children.Add(help);
        proVoice.ItemsSource = ProAccountClient.Voices; AutomationProperties.SetName(proVoice, "Pro 목소리"); FieldRow(proVoiceRow, "Pro 목소리", proVoice); panel.Children.Add(proVoiceRow);
        FieldRow(voiceRow, "목소리 이름 / Voice ID", voice); FieldRow(modelRow, "음성 모델", model); FieldRow(regionRow, "Azure 리소스 지역", region); FieldRow(urlRow, "음성 생성 서버 주소", url); FieldRow(speedRow, "외부 읽기 속도 (1 = 기본)", speed);
        foreach (var row in new[] { voiceRow, modelRow, regionRow, urlRow, speedRow }) panel.Children.Add(row);
        AutomationProperties.SetName(key, "음성 API 키"); FieldRow(keyRow, "음성 API 키", key); keyRow.Children.Add(destination); keyRow.Children.Add(keyStatus);
        var keyButtons = new WrapPanel(); keyButtons.Children.Add(Button("키 저장", SaveKey)); keyButtons.Children.Add(Button("저장한 키 삭제", DeleteKey)); keyRow.Children.Add(keyButtons); panel.Children.Add(keyRow); panel.Children.Add(fallback); panel.Children.Add(feedback);
        panel.Children.Add(Help("키는 설정 파일에 기록하지 않고 Windows에 별도로 보관합니다. ‘이 설정 사용’으로 닫은 뒤 메인 화면의 ‘설정 저장’을 눌러 적용하세요."));
        drafts[settings.Tts.Provider] = settings.Tts with { }; provider.SelectionChanged += ProviderChanged;
        url.LostFocus += (_,_) => UpdateKey(); region.LostFocus += (_,_) => UpdateKey(); provider.SelectedValue = settings.Tts.Provider;
        Closed += (_,_) => { key.Clear(); StopRequested?.Invoke(); };
    }
    VoicePreferences ReadFields() => new() { Provider = selected, Voice = selected == "pro" ? proVoice.SelectedValue?.ToString() ?? ProAccountClient.Voices[0].Id : voice.Text.Trim(), Model = model.Text.Trim(), Region = region.Text.Trim(), Url = url.Text.Trim(), Speed = double.TryParse(speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN, Fallback = selected == "pro" || fallback.IsChecked == true };
    static VoicePreferences Defaults(string id) => id switch
    {
        "openai" => new() { Provider = id, Voice = "alloy", Model = "gpt-4o-mini-tts" },
        "elevenlabs" => new() { Provider = id, Model = "eleven_flash_v2_5" },
        "azure" => new() { Provider = id, Voice = "ko-KR-SunHiNeural", Region = "koreacentral" },
        "http" => new() { Provider = id, Url = "http://127.0.0.1:5002/tts" },
        "pro" => new() { Provider = id, Voice = ProAccountClient.Voices[0].Id },
        _ => new()
    };
    void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || provider.SelectedValue is not string id) return;
        StopRequested?.Invoke(); if (selected.Length > 0) drafts[selected] = ReadFields(); selected = id; loading = true;
        var p = drafts.TryGetValue(id, out var saved) ? saved : Defaults(id);
        voice.Text = p.Voice; model.Text = p.Model; region.Text = p.Region; url.Text = p.Url; speed.Text = p.Speed.ToString("0.##", CultureInfo.InvariantCulture); fallback.IsChecked = p.Fallback; key.Clear();
        voiceRow.Visibility = keyRow.Visibility = id is "windows" or "pro" ? Visibility.Collapsed : Visibility.Visible;
        proVoiceRow.Visibility = id == "pro" ? Visibility.Visible : Visibility.Collapsed;
        proVoice.SelectedValue = p.Voice.Length > 0 ? p.Voice : ProAccountClient.Voices[0].Id;
        fallback.Visibility = id == "windows" ? Visibility.Collapsed : Visibility.Visible; fallback.IsEnabled = id != "pro"; if (id == "pro") fallback.IsChecked = true;
        modelRow.Visibility = id is "openai" or "elevenlabs" ? Visibility.Visible : Visibility.Collapsed;
        regionRow.Visibility = id == "azure" ? Visibility.Visible : Visibility.Collapsed; urlRow.Visibility = id == "http" ? Visibility.Visible : Visibility.Collapsed;
        speedRow.Visibility = id is "windows" or "http" or "pro" ? Visibility.Collapsed : Visibility.Visible;
        help.Text = id switch
        {
            "windows" => "인터넷 없이 Windows에 설치된 목소리로 읽습니다. 목소리와 속도는 앞 화면에서 선택합니다.",
            "pro" => "앞 화면의 ‘Pro 계정 연결·사용량’에서 TurtleMaker 계정을 연결하세요. 읽을 문장을 maker.buffm.com에 전달하며 월 음성 한도가 적용됩니다. 연결·한도·서버 문제 시 Windows 기본 목소리로 읽습니다.",
            "elevenlabs" => "ElevenLabs에서 사용할 Voice ID를 입력하세요. 읽기 속도는 0.7~1.2배입니다.",
            "openai" => "원본과 같은 Speech API를 사용합니다. gpt-4o-mini-tts·tts-1 계열은 2027-01-06 서비스 종료 예정입니다. Realtime 모델은 이 칸에서 사용할 수 없습니다.",
            "azure" => "Azure Speech 리소스의 키와 지역을 입력하세요. 한국어 기본 목소리는 ko-KR-SunHiNeural이며 읽기 속도는 0.5~2배입니다.",
            _ => "서버는 text·voice·language JSON을 받아 WAV 또는 MP3를 반환해야 합니다. HTTPS 또는 이 PC의 HTTP 주소를 사용하며 API 키는 선택 사항입니다. 속도는 서버에서 정합니다."
        };
        feedback.Text = ""; loading = false; UpdateKey();
    }
    void UpdateKey()
    {
        if (selected is "windows" or "pro" || loading) return;
        try { var p = ReadFields(); destination.Text = "키 사용 주소: " + (p.Provider == "elevenlabs" ? "https://api.elevenlabs.io" : ExternalVoice.Endpoint(p).GetLeftPart(UriPartial.Path)); keyStatus.Text = CredentialStore.Read(ExternalVoice.CredentialTarget(p)) != null ? "이 음성 제공자·주소의 키가 저장되어 있어요." : "저장된 음성 API 키가 없어요."; }
        catch (Exception ex) { destination.Text = "주소를 먼저 확인하세요."; keyStatus.Text = ex.Message; }
    }
    void SaveKey(object sender, RoutedEventArgs e)
    {
        try { CredentialStore.Save(ExternalVoice.CredentialTarget(ReadFields()), key.Password); key.Clear(); UpdateKey(); Report("음성 API 키를 Windows에 저장했어요.", false); }
        catch (Exception ex) { Report(ex.Message, true); }
    }
    void DeleteKey(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = ExternalVoice.CredentialTarget(ReadFields());
            if (MessageBox.Show(this, "이 음성 제공자·주소의 저장된 키를 삭제할까요?", "키 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            CredentialStore.Delete(target); key.Clear(); UpdateKey(); Report("저장한 음성 API 키를 삭제했어요.", false);
        }
        catch (Exception ex) { Report(ex.Message, true); }
    }
    VoicePreferences Validated()
    {
        if (key.Password.Length > 0) throw new ArgumentException("입력한 API 키를 먼저 ‘키 저장’으로 보관하세요.");
        var preferences = ReadFields(); preferences.Validate(); return preferences;
    }
    void Preview(object sender, RoutedEventArgs e)
    {
        try { var p = Validated(); Report("음성을 준비하고 재생하고 있어요…", false); PreviewRequested?.Invoke(original with { Tts = p }); }
        catch (Exception ex) { Report(ex.Message, true); }
    }
    void Apply(object sender, RoutedEventArgs e) { try { Result = Validated(); DialogResult = true; } catch (Exception ex) { Report(ex.Message, true); } }
    public void Report(string text, bool warning) { feedback.Text = text; feedback.Foreground = warning ? Brushes.Firebrick : Brushes.DarkGreen; }
}
