using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VoiceAssistant.Desktop;

public partial class PhoneWindow : Window
{
    readonly Func<PhoneNetwork, Task> start;
    readonly Func<Task> stop;
    readonly Func<PhoneServer?> current;
    readonly Func<Task>? startInternet;
    readonly Func<InternetPhone?>? currentInternet;
    bool UsingInternet => ModeInput.SelectedIndex == 0 && startInternet != null;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool changing;
    string? error;
    public PhoneWindow(Func<PhoneNetwork, Task> start, Func<Task> stop, Func<PhoneServer?> current, Func<Task>? startInternet = null, Func<InternetPhone?>? currentInternet = null)
    {
        InitializeComponent(); this.start = start; this.stop = stop; this.current = current;
        this.startInternet = startInternet; this.currentInternet = currentInternet;
        if (startInternet == null || current() != null) ModeInput.SelectedIndex = 1;
        ModeInput.SelectionChanged += (_, _) => Refresh();
        RefreshNetworks();
        if (current() is { } active)
        {
            foreach (PhoneNetwork network in NetworkInput.Items) if (network.Address.Equals(active.Network.Address)) NetworkInput.SelectedItem = network;
            if (System.Net.IPAddress.IsLoopback(active.Network.Address))
            {
                Title = "시험용 · PC 내부 화면 확인";
                PhoneHeading.Text = "PC 내부 시험 · 휴대폰 접속 불가";
                PhoneIntro.Text = "이 주소는 개발 검증용입니다. 실제 연결은 앱의 무료 휴대폰 메뉴에서 켜세요.";
            }
        }
        timer.Tick += (_, _) => Refresh(); timer.Start(); Closed += (_, _) => timer.Stop(); Refresh();
    }
    void RefreshNetworks()
    {
        var selected = (NetworkInput.SelectedItem as PhoneNetwork)?.Address;
        var networks = PhoneNetwork.Available(); NetworkInput.ItemsSource = networks; NetworkInput.SelectedIndex = 0;
        foreach (var network in networks) if (network.Address.Equals(selected)) NetworkInput.SelectedItem = network;
    }
    void Refresh()
    {
        var server = current(); var internet = currentInternet?.Invoke(); bool running = server != null || internet != null;
        ModeInput.IsEnabled = !running && !changing;
        NetworkInput.Visibility = UsingInternet ? Visibility.Collapsed : Visibility.Visible;
        if (server == null || !System.Net.IPAddress.IsLoopback(server.Network.Address))
            PhoneIntro.Text = UsingInternet ? "LTE·5G·다른 와이파이에서도 브라우저로 연결" : "같은 공유기에 연결한 휴대폰과 로컬로 연결";
        NetworkInput.IsEnabled = !running && !changing;
        StartButton.IsEnabled = !running && !changing; StopButton.IsEnabled = ResetButton.IsEnabled = running && !changing;
        AddressText.Text = internet?.Url ?? server?.Url ?? "연결을 켜면 주소가 나옵니다.";
        CodeText.Text = internet == null ? server?.Code ?? "—" : string.Join("-", internet.Code[..4], internet.Code[4..8], internet.Code[8..]);
        var deadline = internet?.PairUntil ?? server?.PairUntil;
        var state = internet != null ? internet.Status : server?.Problem;
        StatusText.Text = changing ? "연결 상태를 바꾸고 있어요…" : error ?? (!running ? "연결 꺼짐" : $"연결된 휴대폰 {internet?.Connected ?? server!.Connected}대 · " + (DateTimeOffset.UtcNow > deadline ? "코드 만료 · 새 코드를 받으세요." : $"코드 유효 시간: {deadline?.ToLocalTime():HH:mm}까지") + (state == null ? "" : "\n" + state));
    }
    async Task Change(Func<Task> action)
    {
        if (changing) return; changing = true; error = null; Refresh();
        try { await action(); }
        catch (Exception ex) { AppPaths.Log("phone-start", ex.GetType().Name); error = UsingInternet ? "인터넷 연결을 켜지 못했어요. PC의 인터넷 연결과 sasarisa.xsw.kr 접속 허용 여부를 확인하세요." : "연결을 켜지 못했어요. 같은 와이파이 연결과 다른 앱의 17845 포트 사용 여부, 저장 공간을 확인하세요."; }
        finally { changing = false; Refresh(); }
    }
    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (UsingInternet) { await Change(startInternet!); return; }
        RefreshNetworks();
        if (NetworkInput.SelectedItem is not PhoneNetwork network) { error = "공유기에 연결된 IPv4 네트워크가 없어요. PC의 와이파이·유선 연결을 확인하세요."; Refresh(); return; }
        await Change(() => start(network));
    }
    async void Stop_Click(object sender, RoutedEventArgs e) => await Change(stop);
    async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var network = current()?.Network; bool internet = currentInternet?.Invoke() != null;
        await Change(async () => { await stop(); if (internet) await startInternet!(); else if (network != null) await start(network); });
    }
}

public partial class App
{
    PhoneServer? phoneServer;
    InternetPhone? internetPhone;
    Task internetPhoneTask = Task.CompletedTask;
    PhoneWindow? phoneWindow;
    void ShowPhone()
    {
        if (phoneWindow != null) { phoneWindow.Show(); phoneWindow.Activate(); return; }
        phoneWindow = new PhoneWindow(StartPhoneAsync, StopPhoneAsync, () => phoneServer, StartInternetPhoneAsync, () => internetPhone) { Owner = window };
        phoneWindow.Closed += (_, _) => phoneWindow = null; phoneWindow.Show();
    }
    async Task StartPhoneAsync(PhoneNetwork network)
    {
        if (closing || phoneServer != null || internetPhone != null) return;
        var server = new PhoneServer(network, Path.Combine(AppPaths.Home, "phone-receipts.json"),
            (command, token) => Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, token, true)).Task.Unwrap(),
            text => RemoteText.Stop(LearnedNames.Strip(text, settings)));
        try { await server.StartAsync(); if (closing) { await server.DisposeAsync(); return; } phoneServer = server; }
        catch { await server.DisposeAsync(); throw; }
    }
    async Task StopPhoneAsync()
    {
        var internet = internetPhone; internetPhone = null;
        if (internet != null)
        {
            internet.Cancel();
            try { await internetPhoneTask.WaitAsync(TimeSpan.FromSeconds(15)); } catch (TimeoutException) { AppPaths.Log("phone-stop", "Internet request shutdown wait expired"); }
            await internet.DisposeAsync();
        }
        var server = phoneServer; phoneServer = null;
        if (server != null) await server.DisposeAsync();
    }
    async Task StartInternetPhoneAsync()
    {
        if (closing || phoneServer != null || internetPhone != null) return;
        var connection = new InternetPhone();
        try
        {
            var ledger = new RemoteLedger(Path.Combine(AppPaths.Home, "internet-phone-receipts.json"));
            await connection.OpenAsync();
            if (closing) { await connection.DisposeAsync(); return; }
            internetPhone = connection;
            var pump = new RemotePump(connection, ledger,
                (command, token) => Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, token, true)).Task.Unwrap(),
                text => connection.Status = text, text => RemoteText.Stop(LearnedNames.Strip(text, settings)));
            internetPhoneTask = Task.Run(async () =>
            {
                try { await pump.RunAsync(connection.Token); }
                catch (Exception ex) { AppPaths.Log("internet-phone", ex.GetType().Name); }
                finally { connection.Status = "인터넷 연결이 중지됐어요. 연결을 끄고 다시 켜세요."; }
            });
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}
