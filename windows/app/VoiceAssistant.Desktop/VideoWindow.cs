using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace VoiceAssistant.Desktop;

// One owned browser surface: replacing a video always stops the previous one.
// User browser tabs and their login/profile data are never enumerated or closed.
public sealed class VideoWindow : Window
{
    readonly WebView2 web = new();
    readonly TextBlock status = new() { Text = UiText.Get("영상을 준비하고 있어요."), Margin = new Thickness(14, 8, 14, 8) };
    Task? initialization;
    bool closed;
    public string CurrentUrl { get; private set; } = "";
    public bool IsClosed => closed;

    public VideoWindow(Func<string, YouTubeVideo, Task>? useBrowser = null)
    {
        Title = UiText.Get("자비스 · 영상"); Width = 1040; Height = 760; MinWidth = 640; MinHeight = 480;
        var layout = new DockPanel(); var bar = new WrapPanel { Margin = new Thickness(8) };
        void Button(string label, Func<Task> action)
        {
            var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(12, 7, 12, 7) };
            button.Click += async (_, _) => { bar.IsEnabled = false; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { bar.IsEnabled = true; } }; bar.Children.Add(button);
        }
        Button("재생 / 일시정지", async () => status.Text = await ControlAsync("playpause", 0, CancellationToken.None));
        Button("10초 뒤로", async () => status.Text = await ControlAsync("seek", -10, CancellationToken.None));
        Button("10초 앞으로", async () => status.Text = await ControlAsync("seek", 10, CancellationToken.None));
        Button("브라우저에서 열기", async () => { await ControlAsync("pause", 0, CancellationToken.None); WindowsActions.OpenUrl(CurrentUrl); });
        if (useBrowser != null)
        {
            async Task Switch(string browser)
            {
                if (!BrowserVideo.IsWatchUrl(CurrentUrl)) throw new InvalidOperationException("영상을 연 뒤 다시 선택하세요.");
                var id = System.Text.RegularExpressions.Regex.Match(new Uri(CurrentUrl).Query, @"(?:^\?|&)v=([A-Za-z0-9_-]{11})(?:&|$)").Groups[1].Value;
                await useBrowser(browser, new YouTubeVideo(id, Title));
            }
            Button("프리미엄 · Chrome으로 전환", () => Switch("chrome"));
            Button("프리미엄 · Edge로 전환", () => Switch("edge"));
        }
        Button("영상 닫기", () => { Close(); return Task.CompletedTask; });
        DockPanel.SetDock(bar, Dock.Top); layout.Children.Add(bar);
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status); layout.Children.Add(web); Content = layout;
        Closed += (_, _) => { closed = true; web.Dispose(); };
    }

    async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Home, "video-browser"));
            if (closed) throw new OperationCanceledException();
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            web.CoreWebView2.Settings.IsWebMessageEnabled = false;
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
            web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; status.Text = UiText.Get("외부 페이지는 ‘브라우저에서 열기’로 이용하세요."); };
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!IsAllowedUrl(e.Uri)) { e.Cancel = true; status.Text = UiText.Get("이 창은 유튜브 영상만 표시해요. 로그인 등은 브라우저에서 이용하세요."); }
            };
            web.CoreWebView2.SourceChanged += (_, _) => { if(IsAllowedUrl(web.CoreWebView2.Source))CurrentUrl=web.CoreWebView2.Source; };
            web.CoreWebView2.DocumentTitleChanged += (_, _) => Title=UiText.Get("자비스 · ")+web.CoreWebView2.DocumentTitle;
            web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (closed) return;
                if (!e.IsSuccess) { status.Text = UiText.Get("영상을 불러오지 못했어요: ") + e.WebErrorStatus; return; }
                try { status.Text = await ControlAsync("play", 0, CancellationToken.None); }
                catch (Exception ex) { if (!closed) status.Text = ex.Message; }
            };
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InvalidOperationException("Microsoft Edge WebView2 Runtime이 필요해요. Microsoft 공식 WebView2 설치 후 다시 실행하세요.");
        }
    }

    public static bool IsAllowedUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && (uri.Host == "www.youtube.com" || uri.Host == "youtube.com" || uri.Host == "consent.youtube.com");

    public async Task OpenAsync(YouTubeVideo video, CancellationToken token)
    {
        if (closed) throw new InvalidOperationException("영상 창이 닫혔어요. 다시 요청하세요.");
        Show(); Activate();
        await (initialization ??= InitializeAsync()).WaitAsync(token);
        token.ThrowIfCancellationRequested(); if (closed) throw new OperationCanceledException();
        // Do not let the old video continue while the next page loads.
        if (CurrentUrl.Length > 0) await ControlAsync("pause", 0, token);
        token.ThrowIfCancellationRequested(); CurrentUrl = video.WatchUrl; Title = UiText.Get("자비스 · ") + video.Title;
        status.Text = UiText.Get("영상을 불러오는 중 · 자동 재생이 막히면 영상의 재생 버튼을 눌러 주세요.");
        web.CoreWebView2.Navigate(CurrentUrl);
    }

    public async Task<string> ControlAsync(string operation, int seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (closed || web.CoreWebView2 == null) throw new InvalidOperationException("먼저 영상을 열어 주세요.");
        if (operation is not ("play" or "pause" or "playpause" or "seek" or "next")) throw new InvalidOperationException("지원하지 않는 영상 동작이에요.");
        if (seconds < -3600 || seconds > 3600) throw new InvalidOperationException("이동할 시간은 1시간 이내로 말해 주세요.");
        // Only fixed operations against the media element; no model-supplied script.
        var script = "(()=>{const v=document.querySelector('video');if(!v)return '영상이 준비되면 재생 버튼을 눌러 주세요.';" +
            (operation switch
            {
                "pause" => "v.pause();return '영상을 일시정지했어요.';",
                "play" => "v.play().catch(()=>{});return '재생을 요청했어요. 재생되지 않으면 영상의 재생 버튼을 눌러 주세요.';",
                "playpause" => "if(!v.paused){v.pause();return '영상을 일시정지했어요.';}v.play().catch(()=>{});return '영상 재생을 요청했어요.';",
                "seek" => "if(!Number.isFinite(v.duration))return '실시간 영상은 시간을 이동할 수 없어요.';v.currentTime=Math.max(0,Math.min(v.duration,v.currentTime+" + seconds + "));return '영상 재생 위치를 바꿨어요.';",
                _ => "const n=document.querySelector('.ytp-next-button');if(!n||n.getAttribute('aria-disabled')==='true')return '다음 영상이 없어요.';n.click();return '다음 영상으로 이동했어요.';"
            }) + "})()";
        var result = await web.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(token);
        return JsonSerializer.Deserialize<string>(result) ?? "영상 제어를 요청했어요.";
    }
}
