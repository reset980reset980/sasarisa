using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public sealed record DeviceOption(string Id, string Label);

public sealed record UiSettings
{
    public string Name { get; set; } = "자비스";
    public Dictionary<string, string[]> LearnedWakes { get; set; } = new();
    public string Device { get; set; } = "";
    public string Activation { get; set; } = "voice";
    public double SilenceSeconds { get; set; } = 1.2;
    public double FollowUpSeconds { get; set; } = 30;
    public int Threads { get; set; } = 2;
    public bool Kws { get; set; } = true;
    public bool WakeHints { get; set; }
    public double InputGain { get; set; } = 1;
    public double KeywordThreshold { get; set; } = 0.15;
    public bool Speak { get; set; } = true;
    public bool ProRemote { get; set; }
    public string GrowTogether { get; set; } = "off";
    public string YouTubePlayer { get; set; } = "embedded";
    public bool PauseInCalls { get; set; } = true;
    public string BargeIn { get; set; } = "auto";
    public string VoiceId { get; set; } = "";
    public string OutputId { get; set; } = "";
    public int SpeechRate { get; set; }
    public int SpeechVolume { get; set; } = 85;
    public VoicePreferences Tts { get; set; } = new();
    public string BrainProvider { get; set; } = "claude";
    public string BrainModel { get; set; } = "";
    public string BrainUrl { get; set; } = "http://127.0.0.1:11434";
    public Dictionary<string, ProviderPreferences> ProviderProfiles { get; set; } = new();
    public string ProjectDirectory { get; set; } = "";
    public string WhisperUrl { get; set; } = "";
    public string WhisperMode { get; set; } = "external";
    public DesktopPreferences Desktop { get; set; } = new();
    public TextPreferences Text { get; set; } = new();
}

public partial class MainWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(49, 93, 231));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(101, 112, 131));
    private string _selectedDevice = "";
    private string _assistantName = "자비스";
    private string _hotkeyChord = "Ctrl + Shift + Space";
    private string _page = "chat";
    private string _activation = "voice";
    private UiSettings _loadedSettings = new();
    private string _stopHotkeyChord = "Ctrl+Alt+Backspace";

    public event Action<string>? CommandSubmitted;
    public event Action? ListenToggled;
    public event Action? WakeRequested;
    public event Action? StopRequested;
    public event Action<UiSettings>? SpeakTestRequested;
    public event Func<UiSettings, string?>? SettingsSaved;
    public event Action<DesktopPreferences>? OverlayPreviewRequested;
    public event Action<UiSettings>? GreetingPreviewRequested;
    public event Action? SetupRequested;
    public event Action? PersonalRequested;
    public event Action<string, DeviceOption>? NameTrainingRequested;
    public event Action? RefreshDevicesRequested;
    public event Action? QuitRequested;
    public event Action? ProAccountChanged;
    public event Action? PhoneRequested;
    void Phone_Click(object sender, RoutedEventArgs e) => PhoneRequested?.Invoke();

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { _apiRequest?.Cancel(); _asideRequest?.Cancel(); };
        CommandInput.TextChanged += (_, _) => InputPlaceholder.Visibility =
            string.IsNullOrEmpty(CommandInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshRegisteredApps();
    }

    public void SetStatus(string title, string detail, bool listening, bool busy, bool paused = false, bool attention = false) => OnUi(() =>
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusPill.Text = busy ? "처리 중" : paused ? "자동 듣기 대기" : attention ? "입력 확인" : listening ? "듣는 중" : "마이크 꺼짐";
        StatusDot.Fill = busy || attention ? new SolidColorBrush(Color.FromRgb(221, 157, 50)) :
            listening ? new SolidColorBrush(Color.FromRgb(47, 156, 122)) : Muted;
        ListenButton.Content = listening ? "듣기 중지" : "듣기 시작";
        ListenButton.Background = listening ? new SolidColorBrush(Color.FromRgb(40, 65, 90)) : Accent;
        ListenButton.BorderBrush = ListenButton.Background;
        StopButton.IsEnabled = listening || busy;
        if (!listening || paused) MicLevel.Value = 0;
    });

    public void SetPartial(string text) => OnUi(() =>
    {
        PartialText.Text = text;
        PartialBorder.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    });

    public void SetLevel(double value) => OnUi(() => MicLevel.Value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0);

    public void AddMessage(string role, string text) => OnUi(() =>
    {
        RemoteExecution.Current?.Capture(role, text);
        if (string.IsNullOrWhiteSpace(text)) return;
        WelcomeCard.Visibility = Visibility.Collapsed;
        bool isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase) || role == "나" || role == "사용자";
        bool isError = role.Equals("error", StringComparison.OrdinalIgnoreCase) || role == "오류";
        bool isSystem = role.Equals("system", StringComparison.OrdinalIgnoreCase) || role == "안내";
        var panel = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock
        {
            Text = isUser ? (RemoteExecution.Current != null ? "휴대폰" : "나") : isError ? "확인이 필요해요" : isSystem ? "안내" : _assistantName,
            Tag = !isUser && !isError && !isSystem ? "assistant-role" : null,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = isUser ? Accent : isError ? Brushes.Firebrick : Muted
        });
        header.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture),
            FontSize = 10,
            Foreground = Muted,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        panel.Children.Add(header);
        panel.Children.Add(new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(28, 43, 65)),
            Padding = new Thickness(0),
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var bubble = new Border
        {
            Background = new SolidColorBrush(isUser ? Color.FromRgb(235, 240, 253) :
                isError ? Color.FromRgb(255, 242, 238) : Color.FromRgb(255, 255, 255)),
            BorderBrush = new SolidColorBrush(isUser ? Color.FromRgb(219, 229, 252) : Color.FromRgb(227, 230, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(17, 13, 17, 15),
            Margin = isUser ? new Thickness(42, 0, 0, 12) : new Thickness(0, 0, 25, 12),
            Child = panel
        };
        HistoryPanel.Children.Add(bubble);
        // Keep the desktop session responsive during long-running conversations.
        while (HistoryPanel.Children.Count > 201) HistoryPanel.Children.RemoveAt(1);
        HistoryScroll.ScrollToEnd();
    });

    public void SetDevices(IReadOnlyList<DeviceOption> devices, string selectedId) => OnUi(() =>
    {
        _selectedDevice = selectedId ?? "";
        DeviceInput.ItemsSource = devices;
        DeviceInput.SelectedValue = _selectedDevice;
        if (DeviceInput.SelectedIndex < 0 && devices.Count > 0) DeviceInput.SelectedIndex = 0;
    });

    public void LoadSettings(UiSettings settings) => OnUi(() =>
    {
        _loadedSettings = settings with { };
        LoadTextPreferences(settings.Text);
        _activation = settings.Activation;
        ApplyAssistantName(settings.Name);
        NameInput.Text = settings.Name;
        _selectedDevice = settings.Device;
        DeviceInput.SelectedValue = settings.Device;
        SelectTag(ActivationInput, settings.Activation);
        SilenceInput.Text = settings.SilenceSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        FollowUpInput.Text = settings.FollowUpSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        ThreadsInput.Text = settings.Threads.ToString(CultureInfo.InvariantCulture);
        KwsInput.IsChecked = settings.Kws;
        WakeHintsInput.IsChecked = settings.WakeHints;
        InputGainInput.Text = settings.InputGain.ToString("0.##", CultureInfo.InvariantCulture);
        ThresholdInput.Text = settings.KeywordThreshold.ToString("0.###", CultureInfo.InvariantCulture);
        SpeakInput.IsChecked = settings.Speak;
        ProRemoteInput.IsChecked = settings.ProRemote;
        SelectTag(GrowTogetherInput, settings.GrowTogether);
        SelectTag(YouTubePlayerInput, settings.YouTubePlayer);
        PauseInCallsInput.IsChecked = settings.PauseInCalls;
        SelectTag(BargeInInput, settings.BargeIn);
        SetSpeechOptions(VoiceInput, SpeechOutput.Options(false), settings.VoiceId);
        SetSpeechOptions(OutputInput, SpeechOutput.Options(true), settings.OutputId);
        SpeechRateInput.Text=settings.SpeechRate.ToString(CultureInfo.InvariantCulture);
        SpeechVolumeInput.Text=settings.SpeechVolume.ToString(CultureInfo.InvariantCulture);
        LoadVoicePreferences(settings.Tts);
        _loadingProviders = true;
        _providerProfiles = new(settings.ProviderProfiles);
        SelectTag(ProviderInput, settings.BrainProvider);
        ModelInput.Text = settings.BrainModel;
        BrainUrlInput.Text = ApiProviders.Find(settings.BrainProvider) is { EditableUrl: false } api ? api.BaseUrl : settings.BrainUrl;
        _draftProvider = settings.BrainProvider;
        ApiKeyInput.Clear();
        _loadingProviders = false;
        ProjectDirectoryInput.Text = settings.ProjectDirectory;
        WhisperUrlInput.Text = settings.WhisperUrl;
        SelectTag(WhisperModeInput, settings.WhisperMode);
        UpdateProviderFields();
        LoadDesktopPreferences(settings.Desktop);
    });

    public void ShowApp() => OnUi(() =>
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    });

    public void SetTimerNotice(string text) => OnUi(() => TimerNotice.Text = text);
    public void SetBrainStatus(string text) => OnUi(() => BrainStatusText.Text = text);
    public void SetRemoteStatus(string text) => OnUi(() => ProRemoteStatus.Text = text);
    public void SetGrowStatus(string text) => OnUi(() => GrowTogetherStatus.Text = text);
    public void SetHotkeyHint(string chord, string stopChord = "Ctrl+Alt+Backspace") => OnUi(() =>
    {
        _hotkeyChord = chord?.Trim() ?? "";
        _stopHotkeyChord = stopChord;
        UpdateHotkeyHints();
    });

    private void OnUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void ChatNav_Click(object sender, RoutedEventArgs e) => ShowPage("chat");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage("settings");
    private void HelpNav_Click(object sender, RoutedEventArgs e) => ShowPage("help");

    private void ShowPage(string page)
    {
        _page = page;
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = page == "help" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "settings" => "나에게 맞는 " + _assistantName, "help" => "일상을 조금 더 가볍게", _ => "무엇을 도와드릴까요?" };
        foreach (var item in new[] { (ChatNav, "chat"), (SettingsNav, "settings"), (HelpNav, "help") })
        {
            item.Item1.Background = page == item.Item2 ? new SolidColorBrush(Color.FromRgb(43, 58, 85)) : Brushes.Transparent;
            item.Item1.Foreground = page == item.Item2 ? Brushes.White : new SolidColorBrush(Color.FromRgb(188, 197, 212));
        }
    }

    private void ApplyAssistantName(string name)
    {
        _assistantName = string.IsNullOrWhiteSpace(name) ? "자비스" : name.Trim();
        Title = _assistantName + " · Windows";
        BrandNameText.Text = _assistantName;
        UpdateHotkeyHints();
        if (_page == "settings") PageTitle.Text = "나에게 맞는 " + _assistantName;
        foreach (var child in HistoryPanel.Children)
            if (child is Border { Child: StackPanel panel } && panel.Children.Count > 0 && panel.Children[0] is Grid header)
                foreach (var part in header.Children)
                    if (part is TextBlock { Tag: "assistant-role" } label) label.Text = _assistantName;
    }

    private void UpdateHotkeyHints()
    {
        bool available = !string.IsNullOrWhiteSpace(_hotkeyChord);
        HotkeyHintText.Text = available ? _hotkeyChord : "단축키 사용 불가";
        SettingsHotkeyHelp.Text = available ? _hotkeyChord + "로 바로 말하기" : "화면의 지금 말하기 버튼을 사용하세요.";
        HelpStartText.Text = "듣기 시작을 누른 뒤 ‘" + _assistantName + "’라고 부르거나, " +
            (available ? _hotkeyChord : "지금 말하기 버튼") + "을 누르고 말하세요. 텍스트로 입력해도 같은 명령을 사용할 수 있어요.";
        WakeButton.ToolTip = available ? "호출 이름 없이 바로 듣습니다. 단축키: " + _hotkeyChord : "호출 이름을 기다리지 않고 바로 듣습니다.";
        StopButton.ToolTip = string.IsNullOrEmpty(_stopHotkeyChord) ? "진행 중인 요청과 답변을 멈춥니다." : "진행 중인 요청과 답변을 멈춥니다. " + _stopHotkeyChord;
        StopHelpText.Text = "처리·음성 출력 중에는 중지 버튼" + (string.IsNullOrEmpty(_stopHotkeyChord) ? "" : " 또는 " + _stopHotkeyChord) + "으로 멈춥니다. ‘그만’ 음성 명령은 듣는 중에 사용할 수 있습니다.";
        WakeButton.Content = _activation=="hold"?"눌러서 말하기 안내":"지금 말하기";
        if(_activation=="hold")
        {
            SettingsHotkeyHelp.Text=available?_hotkeyChord+"를 누른 채 말하고 키를 놓으면 처리합니다.":"다른 호출 방식을 선택하세요. 단축키를 사용할 수 없습니다.";
            WakeButton.ToolTip=SettingsHotkeyHelp.Text;
            HelpStartText.Text="듣기 시작 후 "+SettingsHotkeyHelp.Text+" 한 번에 최대 30초까지 말할 수 있어요. 연속 받아쓰기는 다른 호출 방식에서 사용하세요.";
        }
    }

    private void ListenButton_Click(object sender, RoutedEventArgs e) => ListenToggled?.Invoke();
    private void WakeButton_Click(object sender, RoutedEventArgs e) => WakeRequested?.Invoke();
    private void StopButton_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void SpeakTest_Click(object sender, RoutedEventArgs e)
    {
        if(ReadSpeechOptions() is { } speech)SpeakTestRequested?.Invoke(speech);
    }
    private static void SetSpeechOptions(ComboBox combo,IReadOnlyList<DeviceOption> options,string selected)
    {
        var values=new List<DeviceOption>(options);
        if(!values.Exists(x=>x.Id==selected))values.Add(new(selected,"이전에 선택한 장치/음성 · 현재 없음"));
        combo.ItemsSource=values;combo.SelectedValue=selected;
    }
    private UiSettings? ReadSpeechOptions()
    {
        if(!int.TryParse(SpeechRateInput.Text,out var rate)||rate< -5||rate>5){SettingsError("읽기 속도는 -5~5 사이 정수입니다.",SpeechRateInput);return null;}
        if(!int.TryParse(SpeechVolumeInput.Text,out var volume)||volume<0||volume>100){SettingsError("답변 음량은 0~100 사이 정수입니다.",SpeechVolumeInput);return null;}
        return new UiSettings{VoiceId=(VoiceInput.SelectedItem as DeviceOption)?.Id??"",OutputId=(OutputInput.SelectedItem as DeviceOption)?.Id??"",SpeechRate=rate,SpeechVolume=volume,Tts=voicePreferences with { },BargeIn=(BargeInInput.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"auto"};
    }
    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevicesRequested?.Invoke();
    private void QuitButton_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
    private void Personal_Click(object sender, RoutedEventArgs e) => PersonalRequested?.Invoke();
    private void NameTraining_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 20 || System.Linq.Enumerable.Any(name, char.IsControl)) { SettingsError("호출 이름은 1~20자로 입력하세요.", NameInput); return; }
        NameTrainingRequested?.Invoke(name, DeviceInput.SelectedItem as DeviceOption ?? new(_selectedDevice, "현재 입력 마이크"));
    }
    private void SendButton_Click(object sender, RoutedEventArgs e) => SubmitCommand();

    private void ChooseProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        string current = ProjectDirectoryInput.Text.Trim();
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "코딩 도우미가 작업할 프로젝트 폴더 선택",
            Multiselect = false,
            InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true) ProjectDirectoryInput.Text = dialog.FolderName;
    }

    private void CommandInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        SubmitCommand();
    }

    private void SubmitCommand()
    {
        string command = CommandInput.Text.Trim();
        if (string.IsNullOrEmpty(command)) return;
        CommandInput.Clear();
        CommandSubmitted?.Invoke(command);
        CommandInput.Focus();
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        CommandInput.Text = button.Content?.ToString() ?? "";
        CommandInput.CaretIndex = CommandInput.Text.Length;
        CommandInput.Focus();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var speech=ReadSpeechOptions();if(speech==null)return;
        if (string.IsNullOrWhiteSpace(NameInput.Text) || NameInput.Text.Trim().Length > 20) { SettingsError("호출 이름은 1–20자로 입력해 주세요.", NameInput); return; }
        if (!TryNumber(SilenceInput.Text, 0.4, 3, out double silence)) { SettingsError("말 끝 대기 시간은 0.4–3초로 입력해 주세요.", SilenceInput); return; }
        if (!TryNumber(FollowUpInput.Text, 5, 120, out double followUp)) { SettingsError("후속 대화 유지 시간은 5–120초로 입력해 주세요.", FollowUpInput); return; }
        if (!int.TryParse(ThreadsInput.Text, out int threads) || threads < 1 || threads > 8) { SettingsError("인식 처리 스레드는 1–8 사이의 정수로 입력해 주세요.", ThreadsInput); return; }
        if (!TryNumber(ThresholdInput.Text, 0.01, 0.9, out double threshold)) { SettingsError("호출어 감지 기준은 0.01–0.9로 입력해 주세요.", ThresholdInput); return; }
        if (!TryNumber(InputGainInput.Text, .25, 20, out double inputGain)) { SettingsError("인식 입력 증폭은 0.25~20배로 입력해 주세요.", InputGainInput); return; }
        if (SelectedTag(ProviderInput) == "ollama" && !IsLocalHttpUrl(BrainUrlInput.Text)) { SettingsError("Ollama는 http://127.0.0.1:포트 형식의 로컬 주소를 입력해 주세요.", BrainUrlInput); return; }
        if (!string.IsNullOrWhiteSpace(WhisperUrlInput.Text) && !IsLocalHttpUrl(WhisperUrlInput.Text)) { SettingsError("Whisper는 http://127.0.0.1:포트 형식의 로컬 주소를 입력해 주세요.", WhisperUrlInput); return; }
        try { if (ApiProviders.Find(SelectedTag(ProviderInput)) != null) ApiProviders.Endpoint(SelectedTag(ProviderInput), BrainUrlInput.Text); }
        catch (ArgumentException ex) { SettingsError(ex.Message, BrainUrlInput); return; }
        if (ApiKeyInput.Password.Length > 0) { SettingsError("입력한 API 키를 먼저 ‘키 저장’으로 보관하세요.", ApiKeyInput); return; }
        StoreProviderDraft();
        DesktopPreferences desktop;
        TextPreferences textPreferences;
        try { desktop = ReadDesktopPreferences(); desktop.Validate(SelectedTag(ActivationInput)); }
        catch (ArgumentException ex) { SettingsError(ex.Message, WakeHotkeyInput); return; }
        try { textPreferences = ReadTextPreferences(); }
        catch (ArgumentException ex) { SettingsError(ex.Message, CustomWordsInput); return; }
        var settings = _loadedSettings with
        {
            Name = NameInput.Text.Trim(),
            LearnedWakes = new(_loadedSettings.LearnedWakes),
            Device = (DeviceInput.SelectedItem as DeviceOption)?.Id ?? _selectedDevice,
            Activation = SelectedTag(ActivationInput),
            SilenceSeconds = silence,
            FollowUpSeconds = followUp,
            Threads = threads,
            Kws = KwsInput.IsChecked == true,
            WakeHints = WakeHintsInput.IsChecked == true,
            InputGain = inputGain,
            KeywordThreshold = threshold,
            Speak = SpeakInput.IsChecked == true,
            ProRemote = ProRemoteInput.IsChecked == true,
            GrowTogether = SelectedTag(GrowTogetherInput),
            YouTubePlayer = SelectedTag(YouTubePlayerInput),
            PauseInCalls = PauseInCallsInput.IsChecked == true,
            BargeIn = (BargeInInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto",
            VoiceId=speech.VoiceId, OutputId=speech.OutputId, SpeechRate=speech.SpeechRate, SpeechVolume=speech.SpeechVolume,Tts=speech.Tts,
            BrainProvider = SelectedTag(ProviderInput),
            BrainModel = ModelInput.Text.Trim(),
            BrainUrl = BrainUrlInput.Text.Trim(),
            ProviderProfiles = new(_providerProfiles),
            ProjectDirectory = ProjectDirectoryInput.Text.Trim(),
            WhisperUrl = WhisperUrlInput.Text.Trim(),
            WhisperMode = SelectedTag(WhisperModeInput),
            Desktop = desktop, Text = textPreferences
        };
        var error = SettingsSaved?.Invoke(settings);
        SettingsFeedback.Text = error ?? "설정을 저장했어요. 다음 명령부터 적용됩니다.";
        SettingsFeedback.Foreground = error == null ? new SolidColorBrush(Color.FromRgb(32, 121, 92)) : Brushes.Firebrick;
    }

    private void SettingsError(string message, Control field)
    {
        SettingsFeedback.Text = message;
        SettingsFeedback.Foreground = Brushes.Firebrick;
        field.BringIntoView();
        field.Focus();
    }

    private static bool TryNumber(string text, double min, double max, out double value)
    {
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        return parsed && double.IsFinite(value) && value >= min && value <= max;
    }

    private static bool IsLocalHttpUrl(string text) => Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo);

    private static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private static void SelectTag(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
        combo.SelectedIndex = 0;
    }
}
