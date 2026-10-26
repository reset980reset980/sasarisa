using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal sealed record DisplayPowerResult(bool Confirmed)
{
    public string Summary => Confirmed
        ? "Windows에서 화면 꺼짐을 확인했어요. 마우스를 움직이거나 키를 누르면 다시 켜집니다."
        : "Windows에 화면 끄기를 요청했어요. 꺼짐 상태 알림은 받지 못했어요. 다시 켜려면 마우스를 움직이거나 키를 누르세요.";
}

// Only the display-power system command is sent, to our own window. No shutdown,
// suspend, lock, power-policy change, shell command or broadcast to other apps.
internal sealed class DisplayPower : IDisposable
{
    internal static readonly Guid SessionDisplay = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    const uint WmSysCommand = 0x112, ScMonitorPower = 0xF170;
    const int WmPowerBroadcast = 0x218, PowerSettingChange = 0x8013;
    readonly Func<CancellationToken, Task> request;
    readonly TimeSpan quietDelay, observationTimeout;
    readonly SemaphoreSlim gate = new(1, 1);
    IntPtr registration;
    TaskCompletionSource<bool>? pendingOff;
    bool disposed;
    internal int? ObservedState { get; private set; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    internal DisplayPower(IntPtr window) : this(token => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (window == IntPtr.Zero || !Native.IsWindow(window)) throw new InvalidOperationException("화면 끄기를 요청할 비서 창을 확인하지 못했어요.");
        // A worker thread sends to the responsive application window; the UI thread
        // stays available for cancellation and WM_POWERBROADCAST notifications.
        if (SendMessageTimeout(window, WmSysCommand, (UIntPtr)ScMonitorPower, (IntPtr)2, 0x2 | 0x20, 2000, out _) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows에 화면 끄기 요청을 전달하지 못했어요.");
    }, token), TimeSpan.FromMilliseconds(650), TimeSpan.FromSeconds(2))
    {
        var setting = SessionDisplay;
        registration = RegisterPowerSettingNotification(window, ref setting, 0);
        if (registration == IntPtr.Zero) AppPaths.Log("display-notification", "registration failed: " + Marshal.GetLastWin32Error());
    }

    internal DisplayPower(Func<CancellationToken, Task> request, TimeSpan quietDelay, TimeSpan observationTimeout)
    { this.request = request; this.quietDelay = quietDelay; this.observationTimeout = observationTimeout; }

    internal static int? ReadNotification(int message, IntPtr wParam, IntPtr data)
    {
        if (message != WmPowerBroadcast || wParam.ToInt64() != PowerSettingChange || data == IntPtr.Zero) return null;
        if (Marshal.PtrToStructure<Guid>(data) != SessionDisplay || Marshal.ReadInt32(data, 16) != sizeof(int)) return null;
        var state = Marshal.ReadInt32(data, 20);
        return state is >= 0 and <= 2 ? state : null;
    }

    internal bool HandleMessage(int message, IntPtr wParam, IntPtr data)
    {
        if (disposed || ReadNotification(message, wParam, data) is not { } state) return false;
        Observe(state);
        AppPaths.Log("display-state", state switch { 0 => "off", 1 => "on", _ => "dim" });
        return true;
    }

    internal void Observe(int state)
    {
        if (disposed || state is < 0 or > 2) return;
        ObservedState = state;
        if (state == 0) pendingOff?.TrySetResult(true);
    }

    internal async Task<DisplayPowerResult> OffAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Let the triggering mouse/key release finish before asking Windows.
            await Task.Delay(quietDelay, token);
            ObjectDisposedException.ThrowIf(disposed, this);
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingOff = signal;
            await request(token);
            token.ThrowIfCancellationRequested();
            try { await signal.Task.WaitAsync(observationTimeout, token); return new(true); }
            catch (TimeoutException) { return new(false); }
        }
        finally { pendingOff = null; gate.Release(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; pendingOff?.TrySetCanceled();
        if (registration != IntPtr.Zero) { UnregisterPowerSettingNotification(registration); registration = IntPtr.Zero; }
    }
}
