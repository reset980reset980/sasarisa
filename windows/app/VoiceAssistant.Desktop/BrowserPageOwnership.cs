using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed record BrowserPageWindow(BrowserWindow Window, BrowserView View);
internal sealed record BrowserPageSnapshot(IReadOnlyList<BrowserPageWindow> Windows, bool Complete = true);
internal sealed record BrowserPageIdentity(BrowserWindow Window, string Tab, string Url);

internal static class BrowserPageOwnership
{
    static (IntPtr, int, long, string) Key(BrowserWindow window, BrowserNativeTab tab) => (window.Handle, window.ProcessId, window.Started, tab.Key);
    internal static bool SameUrl(string? actual, string expected) => Uri.TryCreate(actual, UriKind.Absolute, out var a) &&
        Uri.TryCreate(expected, UriKind.Absolute, out var b) && a.Scheme is "http" or "https" && a.AbsoluteUri == b.AbsoluteUri;
    internal static bool DisplayMatches(string display, string full)
    {
        if (SameUrl(display, full)) return true;
        if (display.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(full, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        var fullDisplay = uri.AbsoluteUri[(uri.Scheme.Length + 3)..];
        return display == fullDisplay || uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && display == fullDisplay.TrimEnd('/');
    }
    internal static string? VerifiedAddress(string? display, IEnumerable<string> documents)
    {
        var urls = documents.Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) return display;
        return display != null && urls.Length == 1 && DisplayMatches(display, urls[0]) ? urls[0] : null;
    }
    internal static BrowserPageIdentity? Find(BrowserPageSnapshot before, BrowserPageSnapshot after, string url)
    {
        if (!before.Complete || !after.Complete) return null;
        var old = before.Windows.SelectMany(w => w.View.Tabs.Select(t => Key(w.Window, t))).ToHashSet();
        var current = after.Windows.SelectMany(w => w.View.Tabs.Select(t => Key(w.Window, t))).ToHashSet();
        if (!old.IsSubsetOf(current)) return null; // A concurrent close/replacement makes attribution uncertain.
        var added = after.Windows.SelectMany(w => w.View.Tabs.Where(t => !old.Contains(Key(w.Window, t))).Select(t => (window: w, tab: t))).ToArray();
        if (added.Length != 1 || !added[0].tab.Selected || !SameUrl(added[0].window.View.Address, url)) return null;
        return new(added[0].window.Window, added[0].tab.Key, url);
    }
}

internal interface IBrowserPageHost
{
    Task<BrowserPageSnapshot> CaptureAsync(CancellationToken token);
    void Open(string url);
    Func<CancellationToken, Task<string>>? Own(BrowserPageIdentity page, out IDisposable? lifetime);
}

internal sealed class OwnedLaunch(string label, Func<CancellationToken, Task<string>> close, IDisposable? lifetime = null) : IDisposable
{
    internal string Label => label;
    internal bool Closed { get; private set; }
    internal async Task<string> CloseAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Closed) return "이 항목은 이미 닫았거나 추적이 끝났어요.";
        var result = await close(token); Closed = true; lifetime?.Dispose(); return result;
    }
    public void Dispose() { Closed = true; lifetime?.Dispose(); }
}
