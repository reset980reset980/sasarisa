using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public static class SetupReadiness
{
    public static readonly string[] ModelFiles = { "encoder-epoch-99-avg-1.int8.onnx", "decoder-epoch-99-avg-1.int8.onnx", "joiner-epoch-99-avg-1.int8.onnx", "tokens.txt", "bpe.model" };
    public static bool HasModel(string folder) => ModelFiles.All(name => File.Exists(Path.Combine(folder, name)) && new FileInfo(Path.Combine(folder, name)).Length > 0);
    public static bool HasSpeechEnvironment => File.Exists(AppPaths.Python) && File.Exists(AppPaths.Worker) && HasModel(AppPaths.Model);
    public static string BrainStatus(UiSettings settings)
    {
        if (settings.BrainProvider == "combo") return ComboBrain.Status();
        if (settings.BrainProvider == "claude") return Brain.ClaudeBinary() != null ? "Claude Code 설치됨 · 로그인과 응답은 대화로 확인하세요." : "Claude Code 설치 필요 · 다른 제공자를 선택할 수도 있어요.";
        if (settings.BrainProvider == "codex") return Brain.CodexBinary() != null ? "Codex 설치됨 · 로그인과 응답은 대화로 확인하세요." : "Codex 설치 필요 · 다른 제공자를 선택할 수도 있어요.";
        if (settings.BrainProvider == "ollama") return "Ollama 서버와 설치한 모델이 필요해요. 설정에서 주소와 모델을 확인하세요.";
        if (settings.BrainProvider == "aside") return AsideClient.Binary() != null ? "Aside CLI 설치됨 · 설정에서 계정 연결 상태를 확인하세요." : "Aside Windows CLI 설치 필요 · 설정에 공식 설치 안내가 있어요.";
        var provider = ApiProviders.Find(settings.BrainProvider);
        if (provider == null) return "AI 제공자를 선택하세요.";
        if (string.IsNullOrWhiteSpace(settings.BrainModel)) return provider.Name + " 모델 선택 필요 · 설정에서 모델 목록을 조회하세요.";
        if (provider.RequiresKey && CredentialStore.Read(ApiProviders.CredentialTarget(provider.Id, settings.BrainUrl)) == null) return provider.Name + " API 키 저장 필요 · 설정에서 키를 넣으세요.";
        return provider.Name + " 설정됨 · 설정의 연결 시험으로 응답을 확인하세요.";
    }
}

public sealed class SetupWindow : Window
{
    readonly UiSettings original;
    readonly Func<UiSettings, string?> save;
    readonly TextBox name = Field("비서 이름"), userName = Field("사용자 호칭");
    readonly ComboBox brain = Combo("처음 사용할 AI"), voice = Combo("처음 사용할 목소리"), microphone = Combo("처음 사용할 마이크"), position = Combo("처음 알림 위치");
    readonly TextBlock heading = new(), checks = new(), feedback = new();
    readonly StackPanel first = new(), second = new();
    readonly Button next, back, download, cancelDownload;
    readonly ProgressBar downloadProgress = new() { Minimum = 0, Maximum = 100, Height = 7, Margin = new Thickness(0,10,0,10), Visibility = Visibility.Collapsed };
    readonly TextBlock downloadStatus = new() { TextWrapping = TextWrapping.Wrap };
    CancellationTokenSource? downloadStop;
    bool pageTwo, refreshing, closed;
    public event Action<UiSettings>? VoicePreviewRequested;
    public event Action<DesktopPreferences>? PositionPreviewRequested;
    public event Action? SpeechSetupStarted;
    public SetupWindow(UiSettings settings, Func<UiSettings, string?> save)
    {
        original = settings; this.save = save;
        Title = settings.Name + " · 처음 설정"; Width = 650; Height = 750; MinWidth = 560; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = (Brush)Application.Current.FindResource("CanvasBrush");
        var root = new DockPanel { Margin = new Thickness(28) }; Content = root;
        var top = new StackPanel { Margin = new Thickness(0,0,0,24) }; heading.FontSize = 25; heading.FontWeight = FontWeights.SemiBold;
        top.Children.Add(heading); top.Children.Add(new TextBlock { Text = "나에게 맞게 준비한 뒤, 텍스트나 목소리로 시작하세요.", Foreground = Brushes.SlateGray, Margin = new Thickness(0,8,0,0) }); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var footer = new StackPanel { Margin = new Thickness(0,18,0,0) }; feedback.TextWrapping = TextWrapping.Wrap; feedback.Foreground = Brushes.Firebrick; footer.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        var later = Button("나중에", (_,_) => Close()); back = Button("이전", (_,_) => Page(false)); next = Button("다음", (_,_) => Advance(), true);
        buttons.Children.Add(later); buttons.Children.Add(back); buttons.Children.Add(next); footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new Grid(); content.Children.Add(first); content.Children.Add(second); root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0,0,10,0) });
        AddField(first, "비서를 부를 이름", name); name.Text = settings.Name;
        AddField(first, "비서가 나를 부를 호칭 (선택)", userName); userName.Text = settings.Desktop.UserName;
        var providers = new List<DeviceOption> { new("claude", "Claude Code · 이 PC의 로그인"), new("codex", "Codex · 이 PC의 로그인"), new("aside", "Aside · 브라우저 작업"), new("combo", "조합 · Codex·Claude·Aside 자동 선택"), new("ollama", "Ollama · 로컬 모델") };
        providers.AddRange(ApiProviders.All.Select(x => new DeviceOption(x.Id, x.Name))); brain.ItemsSource = providers; brain.SelectedValue = settings.BrainProvider;
        AddField(first, "생각하고 답할 AI", brain); first.Children.Add(Help("API 제공자의 키·모델과 로컬 서버 주소는 시작 후 설정에서 연결할 수 있어요."));
        voice.ItemsSource = SpeechOutput.Options(false); voice.SelectedValue = settings.VoiceId;
        AddField(first, "답변 목소리", voice); first.Children.Add(Button("이 목소리 들어보기", (_,_) => { try { VoicePreviewRequested?.Invoke(Draft()); } catch (Exception ex) { feedback.Text = ex.Message; } }));
        AddField(second, "입력 마이크", microphone);
        position.ItemsSource = new[] { new DeviceOption("center","가운데"), new DeviceOption("bottomCenter","아래 가운데"), new DeviceOption("bottomRight","오른쪽 아래"), new DeviceOption("bottomLeft","왼쪽 아래"), new DeviceOption("top","위 가운데") }; position.SelectedValue = settings.Desktop.OverlayPosition;
        AddField(second, "알림 위치", position); second.Children.Add(Button("이 위치 미리 보기", (_,_) => PositionPreviewRequested?.Invoke(Draft().Desktop)));
        checks.TextWrapping = TextWrapping.Wrap; checks.LineHeight = 23; checks.Margin = new Thickness(0,18,0,14); second.Children.Add(checks);
        var row = new WrapPanel(); row.Children.Add(Button("다시 검사", async (_,_) => await Refresh())); row.Children.Add(Button("Windows 마이크 설정", (_,_) => Open("ms-settings:privacy-microphone"))); row.Children.Add(Button("설치 폴더 열기", (_,_) => Open(AppPaths.WindowsRoot))); second.Children.Add(row);
        var modelButtons = new WrapPanel(); download = Button("한국어 모델 받기 / 복구", async (_,_) => await DownloadModel()); cancelDownload = Button("다운로드 중지", (_,_) => downloadStop?.Cancel()); cancelDownload.IsEnabled = false;
        modelButtons.Children.Add(download); modelButtons.Children.Add(cancelDownload); second.Children.Add(modelButtons); second.Children.Add(downloadProgress); second.Children.Add(downloadStatus);
        second.Children.Add(Help("한국어 모델은 원본 제공자 k2-fsa에서 약 133 MB를 받습니다. 진행 중 취소할 수 있고 기존 모델은 검증 완료까지 유지합니다. 실행 환경이 없으면 Windows 설치 프로그램을 다시 실행하세요. 마이크는 ‘듣기 시작’ 전까지 녹음하지 않습니다."));
        Page(false); Loaded += async (_,_) => await Refresh(); Closed += (_,_) => { closed = true; downloadStop?.Cancel(); };
    }
    static TextBox Field(string label) { var input = new TextBox { Style = (Style)Application.Current.FindResource("Field") }; AutomationProperties.SetName(input, label); return input; }
    static ComboBox Combo(string label) { var input = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id" }; AutomationProperties.SetName(input, label); return input; }
    static Button Button(string text, RoutedEventHandler handler, bool primary = false)
    {
        var button = new Button { Content = text, Style = (Style)Application.Current.FindResource(primary ? "ActionButton" : "SecondaryButton"), Margin = new Thickness(0,0,8,8), Padding = new Thickness(14,9,14,9), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += handler; return button;
    }
    static TextBlock Help(string text) => new() { Text = text, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,14), LineHeight = 20 };
    static void AddField(StackPanel panel, string label, UIElement input) { panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,7) }); panel.Children.Add(input); panel.Children.Add(new Border { Height = 17 }); }
    void Page(bool showSecond) { pageTwo = showSecond; first.Visibility = showSecond ? Visibility.Collapsed : Visibility.Visible; second.Visibility = showSecond ? Visibility.Visible : Visibility.Collapsed; heading.Text = showSecond ? "2. 이 PC에서 준비하기" : "1. 서로 어떻게 부를까요?"; back.Visibility = showSecond ? Visibility.Visible : Visibility.Collapsed; next.Content = showSecond ? "저장하고 시작" : "다음"; feedback.Text = ""; }
    UiSettings Draft()
    {
        var id = (brain.SelectedItem as DeviceOption)?.Id ?? original.BrainProvider;
        var profile = id == original.BrainProvider ? new ProviderPreferences(original.BrainModel, original.BrainUrl) : original.ProviderProfiles.GetValueOrDefault(id) ?? new("", ApiProviders.DefaultUrl(id));
        var profiles = new Dictionary<string, ProviderPreferences>(original.ProviderProfiles) { [original.BrainProvider] = new(original.BrainModel, original.BrainUrl), [id] = profile };
        var result = original with { Name = name.Text.Trim(), BrainProvider = id, BrainModel = profile.Model, BrainUrl = profile.Url, ProviderProfiles = profiles, VoiceId = (voice.SelectedItem as DeviceOption)?.Id ?? original.VoiceId, Device = (microphone.SelectedItem as DeviceOption)?.Id ?? original.Device,
            Desktop = original.Desktop with { UserName = userName.Text.Trim(), OverlayPosition = (position.SelectedItem as DeviceOption)?.Id ?? original.Desktop.OverlayPosition, SetupComplete = true } };
        SettingsStore.Validate(result); return result;
    }
    void Advance()
    {
        try { var settings = Draft(); if (!pageTwo) { Page(true); _ = Refresh(); return; } var error = save(settings); if (error == null) Close(); else feedback.Text = error; }
        catch (Exception ex) { feedback.Text = ex.Message; }
    }
    async Task DownloadModel()
    {
        if (downloadStop != null) return;
        SpeechSetupStarted?.Invoke(); var own = new CancellationTokenSource(); downloadStop = own;
        download.IsEnabled = next.IsEnabled = back.IsEnabled = false; cancelDownload.IsEnabled = true; downloadProgress.Visibility = Visibility.Visible; downloadStatus.Text = "다운로드를 준비하고 있어요…";
        var lastUpdate = DateTime.MinValue;
        var progress = new Progress<ModelProgress>(value => { if (closed || downloadStop != own) return; if ((DateTime.Now - lastUpdate).TotalMilliseconds < 150 && value.Percent < 100) return; lastUpdate = DateTime.Now; downloadProgress.Value = value.Percent; downloadStatus.Text = $"{value.Stage} · {value.Percent:0}% ({value.Downloaded / 1_000_000.0:0.0}/{value.Total / 1_000_000.0:0.0} MB)"; });
        try { await ModelDownload.InstallAsync(progress, own.Token); if (!closed) { downloadStatus.Text = "한국어 모델을 검증하고 설치했어요. 듣기 시작으로 확인하세요."; await Refresh(); } }
        catch (OperationCanceledException) { if (!closed) downloadStatus.Text = own.IsCancellationRequested ? "다운로드를 취소했어요. 기존 모델을 유지합니다." : "다운로드 시간이 초과됐어요. 다시 시도하세요."; }
        catch (Exception ex) { if (!closed) downloadStatus.Text = "모델을 설치하지 못했어요. " + ex.Message; }
        finally { if (!closed) { download.IsEnabled = next.IsEnabled = back.IsEnabled = true; cancelDownload.IsEnabled = false; } downloadStop = null; own.Dispose(); }
    }
    async Task Refresh()
    {
        if (refreshing) return; refreshing = true; checks.Text = "이 PC의 준비 상태를 확인하고 있어요…";
        var rows = new List<string>();
        try
        {
            rows.Add(File.Exists(AppPaths.Python) ? "✓ 음성 실행 환경 있음" : "• 음성 실행 환경 설치 필요");
            rows.Add(SetupReadiness.HasModel(AppPaths.Model) ? "✓ 한국어 음성 모델 파일 있음" : "• 한국어 음성 모델 설치 필요");
            var selected = (microphone.SelectedItem as DeviceOption)?.Id ?? original.Device;
            try { var devices = await SpeechInput.DevicesAsync(); if (closed) return; microphone.ItemsSource = devices; microphone.SelectedValue = selected; if (microphone.SelectedIndex < 0) microphone.SelectedIndex = 0; rows.Add($"✓ 입력 장치 {Math.Max(0, devices.Count - 1)}개 · 실제 입력은 듣기 시작으로 확인"); }
            catch (Exception ex) { rows.Add("• 마이크 목록을 읽지 못했어요: " + ex.Message); }
            rows.Add("• " + SetupReadiness.BrainStatus(Draft()));
            rows.Add("✓ 앱 실행·타이머는 AI 연결 없이도 사용 가능");
            if (!closed) checks.Text = string.Join("\n", rows);
        }
        catch (Exception ex) { if (!closed) checks.Text = string.Join("\n", rows) + "\n" + ex.Message; }
        finally { refreshing = false; }
    }
    void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { feedback.Text = ex.Message; } }
}
