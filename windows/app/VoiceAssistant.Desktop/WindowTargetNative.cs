using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    public IWindowTargetDriver WindowTargetDriver() => new WindowTargetNative(this);
    sealed class WindowTargetNative(WindowsActions owner) : IWindowTargetDriver
    {
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        ForegroundContext? observedContext;
        static DesktopWindow? ReadWindow(IntPtr window)
        {
            if (!Native.IsWindow(window) || !IsWindowVisible(window) || !IsWindowEnabled(window) || (GetWindowLong(window, -20) & 0x80) != 0) return null;
            // Cloaked windows (including another virtual desktop) are not currently selectable.
            if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) != 0 || cloaked != 0) return null;
            var pid = GetPid(window); if (pid == Environment.ProcessId) return null;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (IsProtectedProcess(process.ProcessName) || process.ProcessName.Equals("VoiceAssistant", StringComparison.OrdinalIgnoreCase)) return null;
                var title = Native.Title(window); if (string.IsNullOrWhiteSpace(title)) return null;
                return new(window, pid, process.StartTime.ToUniversalTime().Ticks, process.ProcessName, title, IsIconic(window));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
        }
        static DesktopWindow Validate(DesktopWindow expected)
        {
            var actual = ReadWindow(expected.Handle);
            if (!WindowTargets.SameWindow(expected, actual)) throw new AgentStoppedException("조회한 창이 닫혔거나 앱·제목이 바뀌어 작업을 멈췄어요. 창 목록을 다시 확인하세요.");
            return actual!;
        }
        public async Task<WindowSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); observedContext = null;
            var before = owner.CaptureForegroundContext();
            var result = await Task.Run(() =>
            {
                var windows = new List<DesktopWindow>(); var truncated = false;
                foreach (var handle in OwnedWindow.VisibleWindows())
                {
                    token.ThrowIfCancellationRequested();
                    if (ReadWindow(handle) is { } item) windows.Add(item);
                    if (windows.Count >= 240) { truncated = true; break; }
                }
                return new WindowSnapshot(windows.OrderBy(x => x.Application, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Handle.ToInt64()).ToArray(), before.Window, truncated);
            }, token);
            token.ThrowIfCancellationRequested(); owner.EnsureForegroundContext(before); observedContext = before; return result;
        }
        public async Task<DesktopWindow> SelectAsync(DesktopWindow window, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var before = observedContext ?? throw new AgentStoppedException("창 목록을 먼저 확인하세요."); observedContext = null;
            owner.EnsureForegroundContext(before); var actual = Validate(window);
            token.ThrowIfCancellationRequested(); owner.EnsureForegroundContext(before);
            if (actual.Minimized) Native.ShowWindow(window.Handle, 9);
            Native.SetForegroundWindow(window.Handle);
            if (Native.GetForegroundWindow() != window.Handle) throw new AgentStoppedException("Windows에서 대상 창으로 전환하지 못했어요. 대상 창을 직접 선택한 뒤 다시 요청하세요.");
            owner.TrackForeground(); var selected = owner.CaptureInputTarget();
            await Task.Delay(120, token);
            owner.RequireInputTarget(selected); actual = Validate(window);
            if (Native.GetForegroundWindow() != window.Handle) throw new AgentStoppedException("전환 중 다른 창이 선택되어 멈췄어요.");
            return actual;
        }
        public async Task<WindowFront> FrontAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var context = owner.CaptureForegroundContext();
            var current = ReadWindow(context.Window); if (current == null) return new(null);
            string? address = null; var addressAvailable = false;
            if (IsBrowserProcess(current.Application))
            {
                try
                {
                    var view = await Task.Run(() => ReadBrowserUi(BrowserWindowFor(current.Handle)), token).WaitAsync(TimeSpan.FromSeconds(5), token);
                    address = view.Address; addressAvailable = address != null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not AgentStoppedException) { }
            }
            token.ThrowIfCancellationRequested(); owner.EnsureForegroundContext(context); _ = Validate(current);
            return new(current, address, addressAvailable);
        }
    }
}
