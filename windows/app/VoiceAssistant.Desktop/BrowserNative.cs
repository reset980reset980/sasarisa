using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    public sealed record ForegroundContext(IntPtr Window, long Version);
    public ForegroundContext CaptureForegroundContext() { TrackForeground(); return new(target, targetVersion); }
    public void EnsureForegroundContext(ForegroundContext expected)
    { TrackForeground(); if (target != expected.Window || targetVersion != expected.Version) throw new AgentStoppedException("요청을 처리하는 동안 대상 창이 바뀌어 멈췄어요."); }
    public static bool IsBrowserProcess(string process) => process.ToLowerInvariant() is "chrome" or "msedge" or "brave" or "firefox";
    sealed record BrowserUiTab(string Key, string Title, bool Selected, AutomationElement Element);
    sealed record BrowserUi(IReadOnlyList<BrowserUiTab> Tabs, string? Address)
    {
        public BrowserView View => new(Tabs.Select(x => new BrowserNativeTab(x.Key, x.Title, x.Selected)).ToArray(), Address);
    }
    static string RuntimeKey(AutomationElement element) => string.Join(".", element.GetRuntimeId());
    public static Uri? BrowserAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var explicitUri) && explicitUri.Scheme is "http" or "https") return explicitUri.UserInfo.Length == 0 ? explicitUri : null;
        // Some omnibox providers omit the scheme. Only a valid hostname can be expanded.
        if (value.Contains("://") || value.Contains('@')) return null;
        var host = value.Split('/', '?', '#')[0].Split(':')[0];
        if (host != "localhost" && !host.Contains('.')) return null;
        try { _ = WebCommands.Host(host); } catch (InvalidOperationException) { return null; }
        return Uri.TryCreate("https://" + value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 ? uri : null;
    }
    static BrowserUi ReadBrowserUi(BrowserWindow window)
    {
        ValidateBrowserWindow(window);
        var root = AutomationElement.FromHandle(window.Handle);
        var tabs = new List<BrowserUiTab>(); var addresses = new List<(string name, string value)>(); var documentAddresses = new List<string>();
        var timer = Stopwatch.StartNew(); int visited = 0;
        void Walk(AutomationElement element, int depth)
        {
            if (depth > 22 || ++visited > 1800 || timer.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("브라우저의 탭 표시를 제한 시간 안에 확인하지 못했어요.");
            var current = element.Current;
            // Never search webpage inputs for browser controls with similar names.
            if (current.ControlType == ControlType.Document)
            {
                // Chromium's document value is its full tree URL; the omnibox
                // value may omit the scheme. Never inspect webpage form fields.
                if (!current.IsOffscreen && element.TryGetCurrentPattern(ValuePattern.Pattern, out var documentValue))
                {
                    var value = ((ValuePattern)documentValue).Current.Value;
                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") documentAddresses.Add(uri.AbsoluteUri);
                }
                return;
            }
            if (current.ControlType == ControlType.TabItem && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            { tabs.Add(new(RuntimeKey(element), current.Name, ((SelectionItemPattern)selection).Current.IsSelected, element)); return; }
            if (current.ControlType == ControlType.Edit && !current.IsPassword && !current.IsOffscreen && element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                var value = ((ValuePattern)valuePattern).Current.Value;
                if (BrowserAddress(value) != null || value.StartsWith("chrome://", StringComparison.Ordinal) || value.StartsWith("edge://", StringComparison.Ordinal) || value.StartsWith("about:", StringComparison.Ordinal)) addresses.Add((current.Name, value));
            }
            for (var child = TreeWalker.ControlViewWalker.GetFirstChild(element); child != null; child = TreeWalker.ControlViewWalker.GetNextSibling(child)) Walk(child, depth + 1);
        }
        Walk(root, 0);
        if (tabs.Count == 0 || tabs.Count(x => x.Selected) != 1) throw new InvalidOperationException("이 브라우저에서 탭 목록을 읽지 못했어요. 브라우저를 업데이트하고 일반 창을 선택하세요.");
        var labeled = addresses.Where(x => x.name.Contains("주소", StringComparison.Ordinal) || x.name.Contains("address", StringComparison.OrdinalIgnoreCase)).ToArray();
        var chosen = labeled.Length == 1 ? labeled : addresses.ToArray();
        var address = chosen.Length == 1 ? chosen[0].value : null;
        address = BrowserPageOwnership.VerifiedAddress(address, documentAddresses);
        // Preserve the provider's address. Host matching parses it separately.
        return new(tabs, address);
    }
    static BrowserWindow BrowserWindowFor(IntPtr window)
    {
        if (!Native.IsWindow(window) || !IsBrowserProcess(Native.ProcessName(window))) throw new InvalidOperationException("조작할 브라우저 창을 먼저 선택하세요.");
        var pid = GetPid(window); using var process = Process.GetProcessById(pid);
        return new(window, pid, process.StartTime.ToUniversalTime().Ticks, Native.Title(window));
    }
    static void ValidateBrowserWindow(BrowserWindow window)
    {
        if (!Native.IsWindow(window.Handle) || GetPid(window.Handle) != window.ProcessId || !IsBrowserProcess(Native.ProcessName(window.Handle))) throw new AgentStoppedException("브라우저 창이 닫혔거나 바뀌었어요.");
        using var process = Process.GetProcessById(window.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != window.Started) throw new AgentStoppedException("원래 브라우저가 종료되어 멈췄어요.");
    }
    public async Task<BrowserAnchor?> CaptureBrowserAnchorAsync(InputTarget? expected, CancellationToken token)
    {
        if (expected == null || !IsBrowserProcess(Native.ProcessName(expected.Window))) return null;
        RequireInputTarget(expected); var browser = BrowserWindowFor(expected.Window);
        var view = await Task.Run(() => ReadBrowserUi(browser), token).WaitAsync(TimeSpan.FromSeconds(5), token);
        RequireInputTarget(expected);
        return new(expected.Window, view.Tabs.Single(x => x.Selected).Key, view.Address);
    }
    public async Task EnsureBrowserAnchorAsync(BrowserAnchor anchor, CancellationToken token)
    {
        var browser = BrowserWindowFor(anchor.Window);
        var view = await Task.Run(() => ReadBrowserUi(browser), token).WaitAsync(TimeSpan.FromSeconds(5), token);
        if (view.Tabs.Single(x => x.Selected).Key != anchor.Tab || view.Address != anchor.Address) throw new AgentStoppedException("요청을 처리하는 동안 브라우저 탭이나 주소가 바뀌어 멈췄어요. 대상 탭에서 다시 요청하세요.");
    }
    public IBrowserDriver BrowserDriver(InputTarget? expected = null, BrowserAnchor? anchor = null) => new BrowserNativeDriver(this, expected, anchor);

    sealed class BrowserNativeDriver : IBrowserDriver
    {
        readonly WindowsActions owner;
        IntPtr contextWindow; long contextVersion;
        readonly Dictionary<IntPtr, string> selected = new();
        public BrowserWindow Initial { get; }
        public BrowserWindow? LastFocused { get; private set; }
        public BrowserNativeDriver(WindowsActions owner, InputTarget? expected, BrowserAnchor? anchor)
        {
            this.owner = owner; owner.TrackForeground(); if (expected != null) owner.RequireInputTarget(expected);
            contextWindow = owner.target; contextVersion = owner.targetVersion;
            Initial = IsBrowserProcess(Native.ProcessName(contextWindow)) ? BrowserWindowFor(contextWindow) : Windows().FirstOrDefault() ?? throw new InvalidOperationException("열려 있는 지원 브라우저를 찾지 못했어요.");
            if (anchor != null && anchor.Window == Initial.Handle) selected[Initial.Handle] = anchor.Tab;
        }
        void Guard(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); owner.TrackForeground();
            if (owner.target != contextWindow || owner.targetVersion != contextVersion) throw new AgentStoppedException("작업 중 다른 창을 선택해 브라우저 동작을 멈췄어요.");
        }
        void AdoptForeground()
        { owner.TrackForeground(); contextWindow = owner.target; contextVersion = owner.targetVersion; }
        void GuardForeground(BrowserWindow window, CancellationToken token)
        { Guard(token); if (Native.GetForegroundWindow() != window.Handle) throw new AgentStoppedException("브라우저에서 다른 창으로 이동해 작업을 멈췄어요."); }
        public IReadOnlyList<BrowserWindow> Windows()
        {
            var windows = new List<BrowserWindow>();
            foreach (var handle in OwnedWindow.VisibleWindows())
            {
                try { if (IsBrowserProcess(Native.ProcessName(handle)) && Native.Title(handle).Length > 0) windows.Add(BrowserWindowFor(handle)); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
            }
            return windows;
        }
        public async Task FocusAsync(BrowserWindow window, CancellationToken token)
        {
            Guard(token); ValidateBrowserWindow(window);
            if (owner.target != window.Handle) { owner.target = window.Handle; owner.targetVersion++; }
            contextWindow = owner.target; contextVersion = owner.targetVersion;
            Native.ShowWindow(window.Handle, 9); Native.SetForegroundWindow(window.Handle);
            await Task.Delay(120, token); Guard(token);
            if (Native.GetForegroundWindow() != window.Handle) throw new AgentStoppedException("브라우저 창으로 전환하지 못해 멈췄어요.");
            LastFocused = window;
        }
        async Task<BrowserUi> UiAsync(BrowserWindow window, CancellationToken token)
        {
            Guard(token); ValidateBrowserWindow(window);
            if (LastFocused?.Handle != window.Handle || Native.GetForegroundWindow() != window.Handle) throw new AgentStoppedException("대상 브라우저가 선택되어 있지 않아요.");
            var view = await Task.Run(() => ReadBrowserUi(window), token).WaitAsync(TimeSpan.FromSeconds(5), token);
            GuardForeground(window, token); var active = view.Tabs.Single(x => x.Selected).Key;
            if (selected.TryGetValue(window.Handle, out var previous) && previous != active) throw new AgentStoppedException("작업 중 선택한 탭이 바뀌어 멈췄어요.");
            selected[window.Handle] = active; return view;
        }
        public async Task<BrowserView> ReadAsync(BrowserWindow window, CancellationToken token) => (await UiAsync(window, token)).View;
        public async Task SelectAsync(BrowserWindow window, string key, CancellationToken token)
        {
            var ui = await UiAsync(window, token);
            var tab = ui.Tabs.SingleOrDefault(x => x.Key == key) ?? throw new AgentStoppedException("원래 탭이 사라졌어요. 목록을 다시 확인하세요.");
            if (tab.Selected) return;
            GuardForeground(window, token);
            if (!tab.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) throw new InvalidOperationException("이 탭은 자동 선택을 지원하지 않아요.");
            ((SelectionItemPattern)pattern).Select(); selected[window.Handle] = key;
            await Task.Delay(100, token); _ = await UiAsync(window, token);
        }
        public async Task CloseAsync(BrowserWindow window, string key, string? expectedAddress, CancellationToken token)
        {
            var ui = await UiAsync(window, token);
            if (ui.Tabs.Single(x => x.Selected).Key != key || ui.Address != expectedAddress) throw new AgentStoppedException("닫으려던 탭이나 주소가 바뀌어 멈췄어요.");
            GuardForeground(window, token);
            // The browser's own shortcut preserves its before-unload confirmation.
            Native.Press(0x57, 0x11);
            await Task.Delay(150, token);
            if (!Native.IsWindow(window.Handle)) { LastFocused = null; selected.Remove(window.Handle); AdoptForeground(); return; }
            Guard(token); var after = await Task.Run(() => ReadBrowserUi(window), token).WaitAsync(TimeSpan.FromSeconds(5), token); Guard(token);
            if (after.Tabs.Any(x => x.Key == key)) throw new AgentStoppedException("탭이 아직 열려 있어 멈췄어요. 브라우저의 저장·나가기 확인창을 직접 확인하세요.");
            selected[window.Handle] = after.Tabs.Single(x => x.Selected).Key;
        }
        public async Task OpenMailAsync(BrowserWindow window, int number, CancellationToken token)
        {
            var before = await UiAsync(window, token); var address = BrowserAddress(before.Address);
            if (address?.IdnHost != "mail.naver.com") throw new InvalidOperationException("순서로 메일 열기는 네이버 메일 목록에서 사용할 수 있어요. 먼저 해당 메일 탭을 선택하세요.");
            var key = before.Tabs.Single(x => x.Selected).Key;
            List<(AutomationElement element, string key, string name)> MailLinks()
            {
                var result = new List<(AutomationElement, string, string)>(); int count = 0; var timer = Stopwatch.StartNew();
                void Walk(AutomationElement element, int depth, bool inDocument)
                {
                    if (depth > 35 || ++count > 2600 || timer.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("메일 목록을 제한 시간 안에 확인하지 못했어요.");
                    var current = element.Current;
                    if (current.ControlType == ControlType.Document) { if (inDocument) return; inDocument = true; }
                    if (inDocument && !current.IsPassword && !current.IsOffscreen && current.ControlType == ControlType.Hyperlink && current.Name.StartsWith("메일 제목", StringComparison.Ordinal)) result.Add((element, RuntimeKey(element), current.Name));
                    for (var child = TreeWalker.ControlViewWalker.GetFirstChild(element); child != null; child = TreeWalker.ControlViewWalker.GetNextSibling(child)) Walk(child, depth + 1, inDocument);
                }
                Walk(AutomationElement.FromHandle(window.Handle), 0, false); return result;
            }
            var links = await Task.Run(MailLinks, token).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (number > links.Count) throw new InvalidOperationException($"현재 화면에서 확인한 메일은 {links.Count}개예요. 메일 목록과 요청 번호를 확인하세요.");
            var currentView = await UiAsync(window, token);
            if (currentView.Address != before.Address || currentView.Tabs.Single(x => x.Selected).Key != key) throw new AgentStoppedException("메일 목록이 바뀌어 멈췄어요.");
            var refreshed = await Task.Run(MailLinks, token).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (refreshed.Count < number || !refreshed.Take(number).Select(x => (x.key, x.name)).SequenceEqual(links.Take(number).Select(x => (x.key, x.name)))) throw new AgentStoppedException("메일 목록 순서나 내용이 바뀌었어요. 다시 요청하세요.");
            GuardForeground(window, token); var chosen = refreshed[number - 1];
            if (chosen.element.Current.IsOffscreen || !chosen.element.Current.IsEnabled || chosen.element.Current.Name != chosen.name) throw new AgentStoppedException("선택할 메일이 바뀌었어요. 다시 요청하세요.");
            if (!chosen.element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) throw new InvalidOperationException("이 메일 링크는 자동 열기를 지원하지 않아요.");
            ((InvokePattern)invoke).Invoke();
        }
    }
}
