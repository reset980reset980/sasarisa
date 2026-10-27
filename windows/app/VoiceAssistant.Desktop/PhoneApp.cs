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
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool changing;
    string? error;
    public PhoneWindow(Func<PhoneNetwork, Task> start, Func<Task> stop, Func<PhoneServer?> current)
    {
        InitializeComponent(); this.start = start; this.stop = stop; this.current = current;
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
        var server = current(); bool running = server != null;
        NetworkInput.IsEnabled = !running && !changing;
        StartButton.IsEnabled = !running && !changing; StopButton.IsEnabled = ResetButton.IsEnabled = running && !changing;
        AddressText.Text = server?.Url ?? "연결을 켜면 주소가 나옵니다."; CodeText.Text = server?.Code ?? "—";
        StatusText.Text = changing ? "연결 상태를 바꾸고 있어요…" : error ?? server?.Problem ?? (server == null ? "연결 꺼짐" : $"연결된 휴대폰 {server.Connected}대 · " + (DateTimeOffset.UtcNow > server.PairUntil ? "코드 만료 · 새 코드를 받으세요." : $"코드 유효 시간: {server.PairUntil.ToLocalTime():HH:mm}까지"));
    }
    async Task Change(Func<Task> action)
    {
        if (changing) return; changing = true; error = null; Refresh();
        try { await action(); }
        catch (Exception ex) { AppPaths.Log("phone-start", ex.GetType().Name); error = "연결을 켜지 못했어요. 같은 와이파이 연결과 다른 앱의 17845 포트 사용 여부, 저장 공간을 확인하세요."; }
        finally { changing = false; Refresh(); }
    }
    async void Start_Click(object sender, RoutedEventArgs e)
    {
        RefreshNetworks();
        if (NetworkInput.SelectedItem is not PhoneNetwork network) { error = "공유기에 연결된 IPv4 네트워크가 없어요. PC의 와이파이·유선 연결을 확인하세요."; Refresh(); return; }
        await Change(() => start(network));
    }
    async void Stop_Click(object sender, RoutedEventArgs e) => await Change(stop);
    async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var network = current()?.Network;
        await Change(async () => { await stop(); if (network != null) await start(network); });
    }
}

public partial class App
{
    PhoneServer? phoneServer;
    PhoneWindow? phoneWindow;
    void ShowPhone()
    {
        if (phoneWindow != null) { phoneWindow.Show(); phoneWindow.Activate(); return; }
        phoneWindow = new PhoneWindow(StartPhoneAsync, StopPhoneAsync, () => phoneServer) { Owner = window };
        phoneWindow.Closed += (_, _) => phoneWindow = null; phoneWindow.Show();
    }
    async Task StartPhoneAsync(PhoneNetwork network)
    {
        if (closing || phoneServer != null) return;
        var server = new PhoneServer(network, Path.Combine(AppPaths.Home, "phone-receipts.json"),
            (command, token) => Dispatcher.InvokeAsync(() => RunRemoteCommandAsync(command, token, true)).Task.Unwrap(),
            text => RemoteText.Stop(LearnedNames.Strip(text, settings)));
        try { await server.StartAsync(); if (closing) { await server.DisposeAsync(); return; } phoneServer = server; }
        catch { await server.DisposeAsync(); throw; }
    }
    async Task StopPhoneAsync()
    {
        var server = phoneServer; phoneServer = null;
        if (server != null) await server.DisposeAsync();
    }
}
