using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record DesktopWindow(IntPtr Handle, int ProcessId, long Started, string Application, string Title, bool Minimized);
public sealed record WindowSnapshot(IReadOnlyList<DesktopWindow> Windows, IntPtr Current, bool Truncated = false);
public sealed record ObservedWindow(string Id, string Application, string Title, bool Current, bool Minimized);
public sealed record WindowPage(IReadOnlyList<ObservedWindow> Windows, int Total, int? Next, bool Truncated);
public sealed record WindowFront(DesktopWindow? Window, string? Address = null, bool BrowserAddressAvailable = false);
public interface IWindowTargetDriver
{
    Task<WindowSnapshot> ReadAsync(CancellationToken token);
    Task<DesktopWindow> SelectAsync(DesktopWindow window, CancellationToken token);
    Task<WindowFront> FrontAsync(CancellationToken token);
}

public sealed class WindowTargets(IWindowTargetDriver driver, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<string, DesktopWindow> observed = new(StringComparer.Ordinal);
    DateTimeOffset expires;
    internal static bool SameWindow(DesktopWindow expected, DesktopWindow? actual) => actual != null && actual with { Minimized = expected.Minimized } == expected;
    public void Invalidate() => observed.Clear();
    public async Task<WindowPage> ListAsync(string filter, int offset, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Invalidate();
        if (filter.Length > 120 || offset is < 0 or > 240) throw new ArgumentException("창 목록의 검색어나 페이지를 확인하세요.");
        var snapshot = await driver.ReadAsync(token); token.ThrowIfCancellationRequested();
        var all = snapshot.Windows.Where(x => filter.Length == 0 || x.Application.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(240).ToArray();
        var page = all.Skip(offset).Take(12).Select(x =>
        {
            var id = Guid.NewGuid().ToString("N"); observed.Add(id, x);
            return new ObservedWindow(id, x.Application, x.Title, x.Handle == snapshot.Current, x.Minimized);
        }).ToArray();
        expires = now().AddMinutes(2);
        return new(page, all.Length, offset + page.Length < all.Length ? offset + page.Length : null, snapshot.Truncated);
    }
    public async Task<DesktopWindow> SelectAsync(string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (now() >= expires || !observed.TryGetValue(id, out var window)) throw new AgentStoppedException("현재 요청에서 방금 조회한 창 ID가 필요해요. 창 목록을 다시 확인하세요.");
        try
        {
            var selected = await driver.SelectAsync(window, token); token.ThrowIfCancellationRequested();
            if (!SameWindow(window, selected)) throw new AgentStoppedException("선택한 창의 결과가 조회한 창과 달라 멈췄어요.");
            return selected;
        }
        finally { Invalidate(); }
    }
    public Task<WindowFront> FrontAsync(CancellationToken token) => driver.FrontAsync(token);
    public static string Data(WindowPage page)
    {
        string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";
        string Format(int limit) => JsonSerializer.Serialize(page with
        {
            Windows = page.Windows.Select(x => x with { Application = Clip(x.Application, 60), Title = Clip(x.Title, limit) }).ToArray(),
            Truncated = page.Truncated || page.Windows.Any(x => x.Title.Length > limit || x.Application.Length > 60)
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var data = Format(180); return data.Length <= 5800 ? data : Format(60);
    }
}
