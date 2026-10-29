using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    void ProAccount_Click(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke();
        void Open() => new ProAccountWindow(NameInput.Text.Trim()) { Owner = this }.ShowDialog();
        if (SettingsFileRequested != null) SettingsFileRequested.Invoke(Open); else Open();
        ProAccountChanged?.Invoke();
    }
}

public sealed class ProAccountWindow : Window
{
    readonly ProAccountClient account;
    readonly string name;
    readonly TextBlock state = Text(UiText.Get("계정 상태를 확인하고 있어요.")), details = Text(""), code = Text(""), feedback = Text("");
    readonly Button link, refresh, cancel, unlink, open;
    CancellationTokenSource? request;
    Uri? approval;
    bool closed;
    static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), LineHeight = 22 };
    static Button Button(string text, RoutedEventHandler clicked)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 5, 8, 5) };
        AutomationProperties.SetName(button, text); button.Click += clicked; return button;
    }
    public ProAccountWindow(string assistantName, ProAccountClient? client = null)
    {
        name = assistantName; account = client ?? ProAccountClient.Shared;
        Title = UiText.Get("Sasarisa Pro 계정"); Width = 630; Height = 560; MinHeight = 500; MinWidth = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(247, 247, 241));
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(feedback); footer.Children.Add(Button(UiText.Get("닫기"), (_, _) => Close()));
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = UiText.Get("Sasarisa Pro 계정"), FontWeight = FontWeights.Bold, FontSize = 23 });
        panel.Children.Add(Text(UiText.Get("TurtleMaker 계정으로 브라우저에서 이 PC의 연결을 승인합니다. 연결 시작 시 호출 이름과 언어가 maker.buffm.com에 전달됩니다. 기기 키는 Windows에 별도로 보관하며 설정 내보내기에 포함하지 않습니다.")));
        state.FontWeight = FontWeights.SemiBold; panel.Children.Add(state); panel.Children.Add(details);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        link = Button(UiText.Get("계정 연결 시작"), Link); refresh = Button(UiText.Get("상태 새로 확인"), Refresh); unlink = Button(UiText.Get("이 PC 연결 해제"), Unlink); cancel = Button(UiText.Get("연결 확인 취소"), (_, _) => request?.Cancel());
        foreach (var button in new[] { link, refresh, unlink, cancel }) actions.Children.Add(button);
        code.FontSize = 20; code.FontWeight = FontWeights.SemiBold; panel.Children.Add(code);
        open = Button(UiText.Get("브라우저에서 승인"), OpenApproval); open.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(open);
        panel.Children.Add(Text(UiText.Get("연결 후 ‘음성 제공자 설정’에서 Sasarisa Pro와 목소리를 선택하세요. 읽을 문장은 Pro 서버로 전달되며, 계정의 음성 사용 한도가 적용됩니다. 연결 안 됨·한도 초과·서버 오류 시 Windows 기본 음성으로 읽습니다.")));
        panel.Children.Add(Text(UiText.Get("창을 닫거나 취소하면 이 PC는 연결 확인을 멈춥니다. 브라우저에서 승인하기 전에 취소했다면 해당 승인 페이지도 닫으세요.")));
        Loaded += async (_, _) => { try { if (account.Linked) await Run(RefreshStatus); else Render(); } catch (Exception ex) { Report(ex.Message, true); } };
        Closed += (_, _) => { closed = true; request?.Cancel(); approval = null; };
        Render();
    }
    void Report(string text, bool error = false) { if (closed) return; feedback.Text = text; feedback.Foreground = error ? Brushes.Firebrick : Brushes.DarkGreen; }
    void Render()
    {
        if (closed) return;
        var linked = account.Linked; var busy = request != null;
        link.IsEnabled = !linked && !busy; refresh.IsEnabled = linked && !busy; unlink.IsEnabled = linked && !busy;
        cancel.IsEnabled = busy; open.IsEnabled = busy && approval != null;
        state.Text = linked ? UiText.Get("이 PC에 계정 연결이 저장되어 있어요.") : UiText.Get("연결된 계정이 없어요. 기본 기능은 그대로 사용할 수 있습니다.");
        if (account.Status is { } s)
            details.Text = (s.Email.Length > 0 ? s.Email + "\n" : "") + UiText.Get("멤버십: ") + (s.Active ? UiText.Get("활성") : UiText.Get("비활성")) +
                (s.Plan.Length > 0 ? " · " + s.Plan : "") + UiText.Get("\nPro 음성: ") + (s.Voice ? UiText.Get("사용 가능") : UiText.Get("기본 목소리 사용")) + $" · 사용량 {s.Used:N0} / {s.Limit:N0}";
        else details.Text = linked ? UiText.Get("회원 상태와 음성 사용량은 ‘상태 새로 확인’으로 확인하세요.") : "";
    }
    async Task Run(Func<CancellationToken, Task> action)
    {
        if (request != null) return;
        using var pending = new CancellationTokenSource(); request = pending; Report("연결을 확인하고 있어요…"); Render();
        try { await action(pending.Token); }
        catch (OperationCanceledException) { Report("연결 확인을 취소했어요."); }
        catch (Exception ex) { Report(ex is InvalidOperationException or TimeoutException or ArgumentException ? ex.Message : "계정 연결을 처리하지 못했어요. 잠시 후 다시 시도하세요.", true); }
        finally { if (request == pending) request = null; approval = null; code.Text = ""; Render(); }
    }
    async Task RefreshStatus(CancellationToken token)
    { await account.RefreshAsync(token); Report("계정 상태와 사용량을 확인했어요."); }
    async void Refresh(object sender, RoutedEventArgs e) => await Run(RefreshStatus);
    async void Link(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        await account.LinkAsync(name, challenge => Dispatcher.Invoke(() =>
        {
            if (closed || token.IsCancellationRequested) return;
            approval = challenge.Page; code.Text = UiText.Get("연결 코드: ") + challenge.Code;
            Report("‘브라우저에서 승인’을 눌러 코드를 확인하고 연결을 승인하세요. 최대 10분 동안 기다립니다."); Render();
        }), token);
        Render(); Report("계정 연결을 저장했어요.");
        await RefreshStatus(token);
    });
    void OpenApproval(object sender, RoutedEventArgs e)
    {
        if (approval == null || request == null) return;
        try { Process.Start(new ProcessStartInfo(approval.AbsoluteUri) { UseShellExecute = true }); }
        catch { Report("브라우저를 열지 못했어요. maker.buffm.com/butler/link에서 위 코드를 입력하세요.", true); }
    }
    async void Unlink(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, UiText.Get("이 PC의 Pro 연결 키를 삭제하고 서버에 연결 해제를 요청할까요? Pro 음성은 기본 목소리로 전환됩니다."), UiText.Get("계정 연결 해제"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run(async token =>
        {
            var revoked = await account.UnlinkAsync(token);
            Report(revoked ? "이 PC의 계정 연결을 해제했어요." : "이 PC의 키는 삭제했지만 서버의 해제 확인을 받지 못했어요. TurtleMaker 계정의 기기 연결도 확인하세요.", !revoked);
        });
    }
}
