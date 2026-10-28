using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    readonly BrowserAutomationWork pageAutomation = new();
    internal IBrowserPageHost PageHost(Action<string>? open = null, Func<IntPtr, bool>? includeWindow = null, Action<string>? trace = null) => new NativePageHost(open ?? OpenUrl, includeWindow, trace, pageAutomation);
    sealed class NativePageHost(Action<string> open, Func<IntPtr, bool>? includeWindow, Action<string>? trace, BrowserAutomationWork automation) : IBrowserPageHost
    {
        public void Open(string url) => open(url);
        public async Task<BrowserPageSnapshot> CaptureAsync(CancellationToken token)
        {
            try
            {
                return await Task.Run(() =>
                {
                    var windows = OwnedWindow.VisibleWindows().Where(h => (includeWindow?.Invoke(h) ?? true) && IsBrowserProcess(Native.ProcessName(h)) && Native.Title(h).Length > 0).ToArray();
                    if (windows.Length > 30) return new BrowserPageSnapshot([], false);
                    var result = new List<BrowserPageWindow>();
                    foreach (var handle in windows)
                    {
                        token.ThrowIfCancellationRequested(); var window = BrowserWindowFor(handle);
                        trace?.Invoke("capture.read.begin"); var view = ReadBrowserUi(window); trace?.Invoke("capture.read.end"); result.Add(new(window, view.View));
                    }
                    return new BrowserPageSnapshot(result);
                }, token).WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new([], false); }
        }
        public Func<CancellationToken, Task<string>>? Own(BrowserPageIdentity page, out IDisposable? lifetime)
        {
            trace?.Invoke("watch.begin"); var watch = OwnedWindow.Watch(page.Window.Handle, page.Window.ProcessId, page.Window.Started, "웹 페이지"); trace?.Invoke("watch.end"); lifetime = watch;
            return watch == null ? null : Close;
            async Task<string> Close(CancellationToken token)
            {
                var alreadyClosed = await automation.RunAsync<string?>(async ct =>
                {
                ct.ThrowIfCancellationRequested();
                if (!watch!.Alive) return "비서가 연 페이지 창이 이미 닫혔거나 추적이 끝나 추가로 닫지 않았어요.";
                trace?.Invoke("close.read.before"); var before = ReadBrowserUi(page.Window);
                if (!before.Tabs.Any(t => t.Key == page.Tab)) return "비서가 연 탭이 이미 닫혀 다른 탭은 그대로 두었어요.";
                ct.ThrowIfCancellationRequested();
                if (!watch.Alive) throw new AgentStoppedException("페이지 창이 바뀌어 닫기를 중단했어요.");
                Native.ShowWindow(page.Window.Handle, 9);
                var tab = before.Tabs.Single(t => t.Key == page.Tab);
                if (!tab.Selected)
                {
                    var select = (SelectionItemPattern)tab.Element.GetCurrentPattern(SelectionItemPattern.Pattern); ct.ThrowIfCancellationRequested();
                    trace?.Invoke("close.select.begin"); select.Select(); trace?.Invoke("close.select.end"); await Task.Delay(100, ct);
                }
                ct.ThrowIfCancellationRequested(); var current = ReadBrowserUi(page.Window);
                tab = current.Tabs.SingleOrDefault(t => t.Key == page.Tab && t.Selected) ?? throw new AgentStoppedException("원래 탭이 바뀌어 닫지 않았어요.");
                if (!watch.Alive || !BrowserPageOwnership.SameUrl(current.Address, page.Url)) throw new AgentStoppedException("비서가 연 탭의 주소나 창이 바뀌어 닫지 않았어요.");
                trace?.Invoke("close.buttons.begin"); var buttons = tab.Element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                    .Where(b => b.Current.IsEnabled && !b.Current.IsOffscreen && System.Text.RegularExpressions.Regex.IsMatch(b.Current.Name, @"^(?:닫기|탭 닫기|Close|Close tab)(?:\s*\(Ctrl\+W\))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
                if (buttons.Length != 1 || !buttons[0].TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) throw new AgentStoppedException("그 탭의 닫기 버튼을 특정하지 못해 닫지 않았어요.");
                trace?.Invoke("close.buttons.end"); ct.ThrowIfCancellationRequested();
                var final = ReadBrowserUi(page.Window);
                if (!watch.Alive || final.Tabs.Single(t => t.Selected).Key != page.Tab || !BrowserPageOwnership.SameUrl(final.Address, page.Url)) throw new AgentStoppedException("닫기 전에 탭이나 주소가 바뀌어 멈췄어요.");
                ct.ThrowIfCancellationRequested();
                trace?.Invoke("close.invoke.begin"); ((InvokePattern)invoke).Invoke(); trace?.Invoke("close.invoke.end");
                return null;
                }, token);
                if (alreadyClosed != null) return alreadyClosed;
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(100, token);
                    if (!watch!.Alive) return "비서가 연 웹페이지 창이 닫힌 것을 확인했어요.";
                    var remaining = await Task.Run(() => ReadBrowserUi(page.Window), token).WaitAsync(TimeSpan.FromSeconds(5), token);
                    if (!remaining.Tabs.Any(t => t.Key == page.Tab)) return "비서가 연 웹페이지 탭이 닫힌 것을 확인했어요.";
                }
                throw new AgentStoppedException("탭이 아직 열려 있어요. 브라우저의 저장·나가기 확인창을 직접 확인해 주세요.");
            }
        }
    }
}
