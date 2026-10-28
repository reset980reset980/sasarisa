using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

internal sealed class WhisperSetupWindow : Window
{
    readonly ComboBox mode = new() { Margin = new Thickness(0, 10, 0, 10), MinHeight = 32 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 8 };
    readonly TextBlock runtimeStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };
    readonly Button runtimeInstall = Button("Microsoft 구성 요소 설치·복구"), runtimeCheck = Button("구성 요소 다시 확인");
    readonly Func<WhisperPrerequisiteStatus> prerequisites;
    readonly Func<IProgress<ModelProgress>, CancellationToken, Task<string>> prepareRuntime;
    readonly Func<string, CancellationToken, Task<int>> launchRuntime;
    readonly Button install = Button("설치·복구"), check = Button("엔진 실행 확인"), apply = Button("설정에 선택"), cancel = Button("닫기");
    CancellationTokenSource? work;
    bool closed, installerActive;
    public string? SelectedMode { get; private set; }
    static Button Button(string label) => new() { Content = label, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 8, 0) };
    public WhisperSetupWindow(string selected, Func<WhisperPrerequisiteStatus>? checkPrerequisites = null,
        Func<IProgress<ModelProgress>, CancellationToken, Task<string>>? prepare = null, Func<string, CancellationToken, Task<int>>? launch = null)
    {
        prerequisites = checkPrerequisites ?? WhisperPrerequisites.CheckSystem;
        prepareRuntime = prepare ?? ((updates, token) => MicrosoftRuntimeSetup.PrepareAsync(System.IO.Path.Combine(AppPaths.Home, "downloads"), updates, token));
        launchRuntime = launch ?? MicrosoftRuntimeSetup.RunAsync;
        Title = "로컬 음성 보정 준비"; Width = 640; Height = 610; MinHeight = 520; MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(24), Background = Brushes.WhiteSmoke }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "이 PC에서 한 번 더 정확하게 듣기", FontSize = 21, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Whisper large-v3-turbo 모델로 명령·받아쓰기를 보정합니다. 음성은 이 PC에서 처리하며 파일로 저장하지 않습니다. 인식 오류가 완전히 없어지는 기능은 아닙니다.", TextWrapping = TextWrapping.Wrap });
        mode.Items.Add(new DeviceOption("cuda", "NVIDIA 그래픽카드 · 다운로드 약 1.56 GB"));
        mode.Items.Add(new DeviceOption("cpu", "CPU · 다운로드 약 0.88 GB · 보정에 시간이 걸릴 수 있음"));
        mode.DisplayMemberPath = "Label"; mode.SelectedValuePath = "Id"; mode.SelectedValue = selected == "cuda" ? "cuda" : "cpu";
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = "NVIDIA 방식은 호환 그래픽카드와 최신 드라이버가 필요합니다. 실제 가속 여부를 실행해서 확인합니다. GitHub ggml-org/whisper.cpp 및 Hugging Face ggerganov의 공식 파일을 검증해 설치합니다.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
        panel.Children.Add(runtimeStatus);
        var runtimeButtons = new WrapPanel(); runtimeButtons.Children.Add(runtimeInstall); runtimeButtons.Children.Add(runtimeCheck); panel.Children.Add(runtimeButtons);
        panel.Children.Add(new TextBlock { Text = "구성 요소가 없거나 손상됐을 때만 설치·복구하세요. Microsoft 설치 창에서 약관과 관리자 승인을 확인합니다. 자비스는 Windows를 자동으로 다시 시작하지 않습니다.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(status); panel.Children.Add(progress);
        var buttons = new WrapPanel(); foreach (var b in new[] { install, check, apply, cancel }) buttons.Children.Add(b); panel.Children.Add(buttons);
        apply.IsEnabled = false; status.Text = "파일 준비 후 ‘설정에 선택’을 누르고, 기본 설정 화면에서 저장하세요.";
        install.Click += async (_, _) => await RunAsync(true);
        check.Click += async (_, _) => await RunAsync(false);
        apply.Click += (_, _) => { SelectedMode = (string)mode.SelectedValue; DialogResult = true; };
        cancel.Click += (_, _) => { if (work != null) { work.Cancel(); status.Text = installerActive ? "대기를 중지했어요. 진행 중인 설치는 Microsoft 창에서 마치거나 취소해 주세요." : "취소하는 중…"; } else Close(); };
        runtimeCheck.Click += (_, _) => CheckPrerequisites(); runtimeInstall.Click += async (_, _) => await RepairRuntimeAsync();
        mode.SelectionChanged += (_, _) => { apply.IsEnabled = false; status.Text = "선택한 방식의 설치 또는 실행 확인이 필요합니다."; };
        Closing += (_, _) => work?.Cancel(); Closed += (_, _) => { closed = true; };
        CheckPrerequisites();
    }
    void CheckPrerequisites()
    {
        var value = prerequisites(); runtimeStatus.Text = value.Detail;
        runtimeStatus.Foreground = value.Ready ? Brushes.DarkGreen : Brushes.Firebrick;
        if (!value.Ready) apply.IsEnabled = false;
    }
    async Task RepairRuntimeAsync()
    {
        if (work != null) return;
        using var own = new CancellationTokenSource(); work = own; apply.IsEnabled = false;
        install.IsEnabled = check.IsEnabled = mode.IsEnabled = runtimeInstall.IsEnabled = runtimeCheck.IsEnabled = false;
        cancel.Content = "취소"; progress.IsIndeterminate = true;
        var updates = new Progress<ModelProgress>(value => { if (!closed && work == own) { progress.IsIndeterminate = false; progress.Value = value.Percent; status.Text = $"Microsoft 설치 파일 받는 중 · {value.Percent:0}%"; } });
        try
        {
            WhisperEngine.Shared.Stop();
            var path = await prepareRuntime(updates, own.Token); own.Token.ThrowIfCancellationRequested();
            if (closed) return;
            status.Text = "Microsoft 설치 창에서 설치·복구를 마쳐 주세요. 이 화면을 닫아도 진행 중인 설치는 Microsoft 창에서 계속됩니다.";
            progress.IsIndeterminate = false; installerActive = true; cancel.Content = "대기 중지";
            int code = await launchRuntime(path, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (!closed) { status.Text = MicrosoftRuntimeSetup.ExitNotice(code); CheckPrerequisites(); }
        }
        catch (OperationCanceledException) { if (!closed) status.Text = installerActive ? "대기를 중지했어요. 설치 상태는 Microsoft 창에서 확인해 주세요." : "Microsoft 구성 요소 받기를 취소했어요."; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { if (!closed) status.Text = "Microsoft 설치 프로그램 실행을 취소했어요."; }
        catch (Exception ex) { if (!closed) status.Text = ex.Message; }
        finally
        {
            work = null; installerActive = false;
            if (!closed) { progress.IsIndeterminate = false; install.IsEnabled = check.IsEnabled = mode.IsEnabled = runtimeInstall.IsEnabled = runtimeCheck.IsEnabled = true; cancel.Content = "닫기"; }
        }
    }
    async Task RunAsync(bool download)
    {
        if (work != null) return;
        var selected = (string)mode.SelectedValue;
        using var own = new CancellationTokenSource(); work = own;
        install.IsEnabled = check.IsEnabled = mode.IsEnabled = runtimeInstall.IsEnabled = runtimeCheck.IsEnabled = apply.IsEnabled = false; cancel.Content = "취소";
        progress.IsIndeterminate = true; status.Text = download ? "파일을 확인하고 필요한 파일을 받습니다…" : "파일 검증 및 엔진 실행 확인 중…";
        var updates = new Progress<ModelProgress>(value => { if (!closed && work == own) { progress.IsIndeterminate = false; progress.Value = value.Percent; status.Text = $"{value.Stage} · {value.Percent:0}%"; } });
        try
        {
            if (download) await WhisperEngine.Shared.InstallAsync(selected, updates, own.Token);
            if (!closed) { progress.IsIndeterminate = true; status.Text = "실제 엔진을 시작해 연결을 확인합니다…"; }
            var result = await WhisperEngine.Shared.CheckAsync(selected, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (!closed) { status.Text = result + " · 마이크 입력 없이 확인했습니다.\n‘설정에 선택’ 후 기본 설정 화면에서 저장하면 적용됩니다."; apply.IsEnabled = true; }
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "취소했어요. 이미 준비된 파일과 기존 설정은 유지됩니다."; }
        catch (Exception ex) { if (!closed) status.Text = ex.Message; }
        finally
        {
            work = null;
            if (!closed) { progress.IsIndeterminate = false; install.IsEnabled = check.IsEnabled = mode.IsEnabled = runtimeInstall.IsEnabled = runtimeCheck.IsEnabled = true; cancel.Content = "닫기"; CheckPrerequisites(); }
        }
    }
}

public partial class MainWindow
{
    public event Action<string>? WhisperSetupRequested;
    void PrepareWhisper_Click(object sender, RoutedEventArgs e) => WhisperSetupRequested?.Invoke(SelectedTag(WhisperModeInput));
    public void SelectWhisperMode(string value) => SelectTag(WhisperModeInput, value);
}

public partial class App
{
    void OpenWhisperSetup(string selected)
    {
        var resume = listening || starting; StopTurn(); StopInput();
        try
        {
            var dialog = new WhisperSetupWindow(selected) { Owner = window };
            if (dialog.ShowDialog() == true && dialog.SelectedMode is { } choice) window.SelectWhisperMode(choice);
        }
        finally { if (resume && !closing) StartListening(); else RefreshStatus(); }
    }
}
