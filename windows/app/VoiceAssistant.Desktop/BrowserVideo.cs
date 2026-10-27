using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed record BrowserVideoState(string? Url, int Tabs, string Tab, bool? Playing, double? Position, double? Duration);
internal interface IBrowserVideoDriver
{
    Task<BrowserWindow?> OpenAsync(string browser, string url, CancellationToken token);
    Task<BrowserVideoState> ReadAsync(BrowserWindow window, CancellationToken token, bool playback = false);
    Task<string> ActAsync(BrowserWindow window, string tab, string operation, int seconds, CancellationToken token);
}

internal sealed class BrowserVideo(IBrowserVideoDriver driver)
{
    BrowserWindow? window;
    string tab = "";
    public bool Active => window != null;
    public bool Opened { get; private set; }
    public async Task<BrowserVideoState> PlaybackAsync(CancellationToken token)
    {
        if (window == null) throw new InvalidOperationException("연결된 영상 창이 없어요.");
        var result = await driver.ReadAsync(window, token, true); Validate(result, tab); return result;
    }
    public static bool IsWatchUrl(string? value)
    {
        var uri = WindowsActions.BrowserAddress(value);
        return uri is { Scheme: "https", AbsolutePath: "/watch" } && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            uri.IdnHost is "www.youtube.com" or "youtube.com" && Regex.IsMatch(uri.Query, @"(?:^\?|&)v=[A-Za-z0-9_-]{11}(?:&|$)");
    }
    public static string? VideoId(string? value) => IsWatchUrl(value) ? Regex.Match(WindowsActions.BrowserAddress(value)!.Query, @"(?:^\?|&)v=([A-Za-z0-9_-]{11})(?:&|$)").Groups[1].Value : null;
    internal static void Validate(BrowserVideoState state, string expectedTab)
    {
        if (state.Tabs != 1 || state.Tab != expectedTab || !IsWatchUrl(state.Url))
            throw new InvalidOperationException("영상 창의 탭이나 주소가 바뀌었어요. 로그인은 브라우저에서 마친 뒤 자비스에게 영상을 다시 요청하세요.");
    }
    public async Task<string> OpenAsync(string browser, YouTubeVideo video, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (window != null)
        {
            var previous = window;
            BrowserVideoState? state = null;
            try { state = await driver.ReadAsync(previous, token); Validate(state, tab); }
            catch (Exception ex) when (ex is InvalidOperationException or AgentStoppedException or System.Windows.Automation.ElementNotAvailableException) { }
            if (state != null && state.Tabs == 1 && state.Tab == tab && IsWatchUrl(state.Url))
            {
                await driver.ActAsync(previous, tab, "pause", 0, token);
                await driver.ActAsync(previous, tab, "close", 0, token);
            }
            window = null;
        }
        var opened = await driver.OpenAsync(browser, video.WatchUrl, token);
        Opened = true;
        if (opened == null) return "브라우저에 영상을 열었어요. 로그인 또는 프로필 선택을 마쳐 주세요. 영상 창을 확인하지 못해 음성 제어는 연결하지 않았어요.";
        var view = await driver.ReadAsync(opened, token);
        Validate(view, view.Tab); window = opened; tab = view.Tab;
        return "선택한 브라우저에 영상을 열었어요. 자동 재생이 멈추면 영상의 재생 버튼을 눌러 주세요.";
    }
    public async Task<string> ControlAsync(string operation, int seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (window == null) return "먼저 자비스에게 브라우저에서 유튜브 영상을 틀어 달라고 말해 주세요.";
        var own = window; var state = await driver.ReadAsync(own, token); Validate(state, tab);
        var result = await driver.ActAsync(own, tab, operation, seconds, token);
        if (operation == "close") { window = null; Opened = false; }
        return result;
    }
    public void Forget() { window = null; tab = ""; Opened = false; }
}

internal static class VideoBrowserLaunch
{
    public static string? Binary(string browser)
    {
        var program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = browser switch
        {
            "chrome" => new[] { Path.Combine(program, "Google/Chrome/Application/chrome.exe"), Path.Combine(x86, "Google/Chrome/Application/chrome.exe"), Path.Combine(local, "Google/Chrome/Application/chrome.exe") },
            "edge" => new[] { Path.Combine(x86, "Microsoft/Edge/Application/msedge.exe"), Path.Combine(program, "Microsoft/Edge/Application/msedge.exe") },
            _ => throw new ArgumentException("Chrome 또는 Edge를 선택하세요.")
        };
        return paths.FirstOrDefault(File.Exists);
    }
    public static ProcessStartInfo Info(string browser, string url)
    {
        if (url != "https://www.youtube.com/" && !BrowserVideo.IsWatchUrl(url)) throw new ArgumentException("유튜브 영상 주소를 확인하세요.");
        var executable = Binary(browser) ?? throw new InvalidOperationException((browser == "chrome" ? "Chrome" : "Edge") + "가 설치되지 않았어요. 설정에서 다른 브라우저를 선택하세요.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        // A normal browser window uses its own existing profile and sign-in.
        // Never copy cookies or enable debugging, incognito or a security override.
        info.ArgumentList.Add("--new-window"); info.ArgumentList.Add(url); return info;
    }
    public static void Login(string browser) { using var process = Process.Start(Info(browser, "https://www.youtube.com/")); }
}
