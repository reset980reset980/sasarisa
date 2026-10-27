using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    internal IBrowserVideoDriver VideoDriver() => new BrowserVideoNative();
    sealed class BrowserVideoNative : IBrowserVideoDriver
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        static bool Match(string name, string pattern) => Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        sealed record Controls(BrowserVideoState State, AutomationElement? Toggle, AutomationElement? Next, AutomationElement? Slider);
        static Controls Read(BrowserWindow window, bool controls = true)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return ReadOnce(window, controls); }
                catch (ElementNotAvailableException) when (attempt < 2) { }
            }
        }
        static Controls ReadOnce(BrowserWindow window, bool controls)
        {
            var ui = ReadBrowserUi(window); var tab = ui.Tabs.Single(x => x.Selected).Key;
            if (!controls || ui.Tabs.Count != 1 || !BrowserVideo.IsWatchUrl(ui.Address)) return new(new(ui.Address, ui.Tabs.Count, tab, null, null, null), null, null, null);
            var root = AutomationElement.FromHandle(window.Handle);
            var found = new List<AutomationElement>(); int count = 0; var time = Stopwatch.StartNew();
            void Walk(AutomationElement element, int depth)
            {
                if (depth > 40 || ++count > 4000 || time.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("영상 제어 항목을 제한 시간 안에 확인하지 못했어요.");
                var current = element.Current;
                if (current.ControlType is var kind && (kind == ControlType.Button || kind == ControlType.Slider) && !current.IsOffscreen && current.IsEnabled) found.Add(element);
                for (var child = TreeWalker.ControlViewWalker.GetFirstChild(element); child != null; child = TreeWalker.ControlViewWalker.GetNextSibling(child)) Walk(child, depth + 1);
            }
            Walk(root, 0);
            AutomationElement? One(string pattern, ControlType kind)
            { var matches = found.Where(x => x.Current.ControlType == kind && Match(x.Current.Name, pattern)).ToArray(); return matches.Length == 1 ? matches[0] : null; }
            var toggle = One(@"^(?:재생|일시정지|일시중지|일시 정지|일시 중지|Play|Pause)(?:\s*\(k\)|\s+(?:키보드 단축키|Keyboard shortcut)\s+k)?$", ControlType.Button);
            var next = One(@"^(?:다음|Next)(?:\s*\(SHIFT\+n\))?$", ControlType.Button);
            var slider = One(@"^(?:탐색 슬라이더|Seek slider|동영상 탐색 슬라이더)$", ControlType.Slider);
            bool? playing = toggle == null ? null : Match(toggle.Current.Name, @"^(?:Pause|일시)"); double? position = null, duration = null;
            if (slider != null && slider.TryGetCurrentPattern(RangeValuePattern.Pattern, out var value))
            { var range = ((RangeValuePattern)value).Current; if (!range.IsReadOnly && double.IsFinite(range.Maximum) && range.Maximum > 0) { position = range.Value; duration = range.Maximum; } }
            return new(new(ui.Address, ui.Tabs.Count, tab, playing, position, duration), toggle, next, slider);
        }
        public async Task<BrowserWindow?> OpenAsync(string browser, string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var before = OwnedWindow.VisibleWindows(true);
            using var launched = Process.Start(VideoBrowserLaunch.Info(browser, url));
            var processName = browser == "chrome" ? "chrome" : "msedge";
            string lastFailure = "새 영상 창을 찾지 못했어요.";
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(300, token);
                var candidates = OwnedWindow.VisibleWindows(true).Where(h => !before.Contains(h) && Native.Title(h).Length > 0 && Native.ProcessName(h).Equals(processName, StringComparison.OrdinalIgnoreCase)).ToArray();
                // A brokered browser launch can use an existing process. Claim only
                // one newly created window with exactly the requested watch URL.
                if (candidates.Length != 1) { lastFailure = "새 브라우저 창 수: " + candidates.Length; continue; }
                try
                {
                    var candidate = BrowserWindowFor(candidates[0]);
                    Native.ShowWindow(candidate.Handle, 9);
                    var state = await Task.Run(() => Read(candidate, false).State, token).WaitAsync(TimeSpan.FromSeconds(4), token);
                    if (state.Tabs == 1 && BrowserVideo.VideoId(url) == BrowserVideo.VideoId(state.Url)) return candidate;
                    lastFailure = "영상 주소 또는 탭 수가 아직 일치하지 않아요. 탭 수: " + state.Tabs;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or TimeoutException) { lastFailure = ex.GetType().Name + ": " + ex.Message; }
            }
            AppPaths.Log("video-browser-open", lastFailure);
            return null;
        }
        public Task<BrowserVideoState> ReadAsync(BrowserWindow window, CancellationToken token, bool playback = false) => Task.Run(() => Read(window, playback).State, token).WaitAsync(TimeSpan.FromSeconds(5), token);
        public async Task<string> ActAsync(BrowserWindow window, string tab, string operation, int seconds, CancellationToken caller)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller); deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var token = deadline.Token;
            return await Task.Run(() =>
            {
            token.ThrowIfCancellationRequested();
            if (operation is not ("play" or "pause" or "playpause" or "seek" or "next" or "close") || seconds < -3600 || seconds > 3600) throw new ArgumentException("영상 제어 요청을 확인하세요.");
            var current = Read(window, operation != "close"); BrowserVideo.Validate(current.State, tab); token.ThrowIfCancellationRequested();
            // Recheck the browser chrome after inspecting the page controls.
            BrowserVideo.Validate(Read(window, false).State, tab);
            token.ThrowIfCancellationRequested();
            if (operation == "close")
            {
                if (!PostMessage(window.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("영상 창 닫기를 요청하지 못했어요.");
                return "자비스가 연 브라우저 영상 창의 닫기를 요청했어요.";
            }
            if (operation == "seek")
            {
                // Chrome exposes a writable range, but SetValue did not change
                // the observed YouTube playback position in the native check.
                throw new InvalidOperationException("브라우저 재생의 시간 이동은 아직 지원하지 않아요. 영상의 탐색 막대에서 직접 이동해 주세요.");
            }
            if (operation == "pause" && current.State.Playing == false) return "영상이 이미 일시정지 상태예요.";
            if (operation == "play" && current.State.Playing == true) return "영상이 이미 재생 상태예요.";
            var control = operation == "next" ? current.Next : current.Toggle;
            if (control == null || !control.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) throw new InvalidOperationException("영상의 재생 버튼을 확인하지 못했어요. 브라우저에서 영상을 먼저 재생해 주세요.");
            token.ThrowIfCancellationRequested(); ((InvokePattern)invoke).Invoke();
            return operation == "next" ? "브라우저에서 다음 영상 재생을 요청했어요." : "브라우저 영상의 재생 상태 변경을 요청했어요.";
            }, token).WaitAsync(token);
        }
    }
}
