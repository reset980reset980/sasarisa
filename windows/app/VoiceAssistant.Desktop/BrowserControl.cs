using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record BrowserWindow(IntPtr Handle, int ProcessId, long Started, string Title);
public sealed record BrowserNativeTab(string Key, string Title, bool Selected);
public sealed record BrowserView(IReadOnlyList<BrowserNativeTab> Tabs, string? Address);
public sealed record BrowserTab(string Id, BrowserWindow Window, string Key, int Number, string Title, string? Url, bool Selected);
public sealed record BrowserAnchor(IntPtr Window, string Tab, string? Address);
public sealed record BrowserPage(int Total, int Page, int Pages, IReadOnlyList<BrowserTab> Tabs);

public static class BrowserObservation
{
    public static string Data(BrowserPage page)
    {
        string Clip(string value, int count) => value.Length <= count ? value : value[..count] + "…";
        string Format(bool shortText) => JsonSerializer.Serialize(new { total = page.Total, page = page.Page, pages = page.Pages, tabs = page.Tabs.Select(x => new { id = x.Id, number = x.Number, title = Clip(x.Title, shortText ? 40 : 80), url = shortText ? null : x.Url == null ? null : Clip(x.Url, 200), host = WindowsActions.BrowserAddress(x.Url)?.IdnHost, window = Clip(x.Window.Title, shortText ? 20 : 40), selected = x.Selected, truncated = shortText || x.Title.Length > 80 || x.Url?.Length > 200 || x.Window.Title.Length > 40 }) }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var data = Format(false); if (data.Length > 5200) data = Format(true);
        if (data.Length > 5800) data = JsonSerializer.Serialize(new { total = page.Total, page = page.Page, pages = page.Pages, tabs = page.Tabs.Select(x => new { id = x.Id, number = x.Number, host = WindowsActions.BrowserAddress(x.Url)?.IdnHost, selected = x.Selected, truncated = true }) });
        return data;
    }
}

public interface IBrowserDriver
{
    BrowserWindow Initial { get; }
    BrowserWindow? LastFocused { get; }
    IReadOnlyList<BrowserWindow> Windows();
    Task FocusAsync(BrowserWindow window, CancellationToken token);
    Task<BrowserView> ReadAsync(BrowserWindow window, CancellationToken token);
    Task SelectAsync(BrowserWindow window, string key, CancellationToken token);
    Task CloseAsync(BrowserWindow window, string key, string? expectedAddress, CancellationToken token);
    Task OpenMailAsync(BrowserWindow window, int number, CancellationToken token);
}

public sealed class BrowserControl(Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<string, (BrowserTab tab, DateTimeOffset at)> known = new();
    IReadOnlyList<BrowserTab>? listing; DateTimeOffset listedAt; IntPtr listedWindow; bool listedAll;
    public BrowserWindow? LastTarget { get; private set; }
    public async Task<BrowserPage> PageAsync(IBrowserDriver driver, bool all, int page, CancellationToken token)
    {
        page = Math.Max(1, page);
        if (page == 1)
        { listing = await ListAsync(driver, all, token); listedAt = now(); listedWindow = driver.Initial.Handle; listedAll = all; }
        else if (listing == null || listedWindow != driver.Initial.Handle || listedAll != all || now() < listedAt || now() - listedAt >= TimeSpan.FromMinutes(2)) throw new InvalidOperationException("탭 목록을 먼저 다시 확인하세요. ‘탭 목록’ 또는 ‘전체 탭 목록’이라고 말하세요.");
        token.ThrowIfCancellationRequested(); var pages = Math.Max(1, (listing!.Count + 5) / 6);
        if (page > pages) throw new InvalidOperationException($"탭 목록은 {pages}페이지까지 있어요.");
        return new(listing.Count, page, pages, listing.Skip((page - 1) * 6).Take(6).ToArray());
    }
    static BrowserNativeTab Selected(BrowserView view) => view.Tabs.SingleOrDefault(x => x.Selected) ?? throw new InvalidOperationException("선택된 브라우저 탭을 확인하지 못했어요.");
    static void SameTab(BrowserView view, string key)
    { if (Selected(view).Key != key) throw new AgentStoppedException("작업 중 브라우저 탭이 바뀌어 멈췄어요. 대상 탭을 선택하고 다시 요청하세요."); }
    BrowserTab Remember(BrowserWindow window, BrowserNativeTab tab, int number, string? address, bool selected)
    {
        var result = new BrowserTab(Guid.NewGuid().ToString(), window, tab.Key, number, tab.Title, address, selected);
        known[result.Id] = (result, now());
        foreach (var key in known.Where(x => now() - x.Value.at >= TimeSpan.FromMinutes(2)).Select(x => x.Key).ToArray()) known.Remove(key);
        while (known.Count > 4096) known.Remove(known.Keys.First());
        return result;
    }
    BrowserTab Find(string id)
    {
        if (!known.TryGetValue(id, out var entry) || now() < entry.at || now() - entry.at >= TimeSpan.FromMinutes(2)) throw new InvalidOperationException("탭 목록이 오래됐거나 해당 탭을 확인하지 못했어요. 목록을 다시 요청하세요.");
        return entry.tab;
    }
    public async Task<IReadOnlyList<BrowserTab>> ListAsync(IBrowserDriver driver, bool all, CancellationToken token)
    {
        var windows = all ? driver.Windows().OrderByDescending(x => x.Handle == driver.Initial.Handle).ToArray() : new[] { driver.Initial };
        if (windows.Length > 30) throw new InvalidOperationException("브라우저 창이 30개보다 많아요. 창을 나누어 확인하세요.");
        var result = new List<BrowserTab>();
        try
        {
            foreach (var window in windows)
            {
                token.ThrowIfCancellationRequested(); await driver.FocusAsync(window, token);
                var initial = await driver.ReadAsync(window, token); var original = Selected(initial);
                if (initial.Tabs.Count > 120) throw new InvalidOperationException("이 창의 탭이 120개보다 많아요. 창을 나누어 확인하세요.");
                for (int index = 0; index < initial.Tabs.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); var tab = initial.Tabs[index];
                    await driver.SelectAsync(window, tab.Key, token);
                    var view = await driver.ReadAsync(window, token); SameTab(view, tab.Key);
                    if (!view.Tabs.Select(x => x.Key).SequenceEqual(initial.Tabs.Select(x => x.Key))) throw new AgentStoppedException("확인 중 탭 목록이나 순서가 바뀌어 멈췄어요. 다시 요청하세요.");
                    result.Add(Remember(window, view.Tabs.Single(x => x.Key == tab.Key), index + 1, view.Address, tab.Key == original.Key));
                }
                token.ThrowIfCancellationRequested(); await driver.SelectAsync(window, original.Key, token);
            }
            token.ThrowIfCancellationRequested(); await driver.FocusAsync(driver.Initial, token);
            foreach (var tab in result) known[tab.Id] = (tab, now());
            return result;
        }
        finally { LastTarget = driver.LastFocused; }
    }
    public async Task<string> SelectAsync(IBrowserDriver driver, string id, bool close, CancellationToken token)
    {
        var tab = Find(id);
        if (close && tab.Url == null) throw new InvalidOperationException("주소를 확인하지 못한 탭은 목록 ID로 닫지 않아요. 해당 탭을 선택한 뒤 번호로 닫아 주세요.");
        try
        {
            await driver.FocusAsync(tab.Window, token);
            var before = await driver.ReadAsync(tab.Window, token);
            if (!before.Tabs.Any(x => x.Key == tab.Key)) throw new AgentStoppedException("원래 탭이 닫혔어요. 탭 목록을 다시 확인하세요.");
            await driver.SelectAsync(tab.Window, tab.Key, token);
            var selected = await driver.ReadAsync(tab.Window, token); SameTab(selected, tab.Key);
            if (!string.Equals(selected.Address, tab.Url, StringComparison.Ordinal)) throw new AgentStoppedException("그 탭의 주소가 바뀌어 실행하지 않았어요. 목록을 다시 확인하세요.");
            if (!close) return $"‘{tab.Title}’ 탭을 선택했어요.";
            await driver.CloseAsync(tab.Window, tab.Key, tab.Url, token); known.Remove(id);
            return $"‘{tab.Title}’ 탭을 닫았어요.";
        }
        finally { LastTarget = driver.LastFocused; }
    }
    public async Task<string> NthAsync(IBrowserDriver driver, int number, bool close, CancellationToken token)
    {
        if (number is < 1 or > 100) throw new InvalidOperationException("탭 번호는 1~100 사이로 지정하세요.");
        var window = driver.Initial;
        try
        {
            await driver.FocusAsync(window, token); var view = await driver.ReadAsync(window, token);
            if (number > view.Tabs.Count) throw new InvalidOperationException($"이 창에는 탭이 {view.Tabs.Count}개 있어요.");
            var tab = view.Tabs[number - 1]; await driver.SelectAsync(window, tab.Key, token);
            var current = await driver.ReadAsync(window, token); SameTab(current, tab.Key);
            if (close) { await driver.CloseAsync(window, tab.Key, current.Address, token); return $"{number}번째 ‘{tab.Title}’ 탭을 닫았어요."; }
            return $"{number}번째 ‘{tab.Title}’ 탭을 선택했어요.";
        }
        finally { LastTarget = driver.LastFocused; }
    }
    public async Task<string> CloseSiteAsync(IBrowserDriver driver, string host, CancellationToken token)
    {
        host = WebCommands.Host(host); int closed = 0;
        try
        {
            var tabs = await ListAsync(driver, false, token);
            var matches = tabs.Where(x => WebCommands.MatchesHost(x.Url, host)).ToArray();
            if (matches.Length == 0)
            {
                tabs = await ListAsync(driver, true, token);
                matches = tabs.Where(x => WebCommands.MatchesHost(x.Url, host)).ToArray();
            }
            foreach (var tab in matches.OrderByDescending(x => x.Number))
            {
                token.ThrowIfCancellationRequested(); await SelectAsync(driver, tab.Id, true, token); closed++;
            }
            return closed == 0 ? $"{host}에서 닫을 탭을 찾지 못했어요." : $"{host} 탭 {closed}개를 닫았어요.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (closed > 0) { throw new AgentStoppedException($"탭 {closed}개를 닫은 뒤 멈췄어요. {ex.Message}"); }
        finally { LastTarget = driver.LastFocused; }
    }
    public async Task<string> MailAsync(IBrowserDriver driver, int number, CancellationToken token)
    {
        if (number is < 1 or > 100) throw new InvalidOperationException("메일 번호는 1~100 사이로 지정하세요.");
        try { await driver.FocusAsync(driver.Initial, token); await driver.OpenMailAsync(driver.Initial, number, token); return $"{number}번째 메일 열기를 요청했어요."; }
        finally { LastTarget = driver.LastFocused; }
    }
}
