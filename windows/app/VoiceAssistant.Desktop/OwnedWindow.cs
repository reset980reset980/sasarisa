using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record OwnedWindow(IntPtr Handle, int ProcessId, long ProcessStarted, string Label) : IDisposable
{
    delegate void EventCallback(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, EventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    IntPtr destroyHook;
    EventCallback? destroyCallback;
    GCHandle callbackRoot;
    bool destroyed, released;
    delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static HashSet<IntPtr> VisibleWindows()
    {
        var result = new HashSet<IntPtr>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) result.Add(h); return true; }, IntPtr.Zero);
        return result;
    }
    public static async Task<OwnedWindow?> FindNewAsync(HashSet<IntPtr> before, Process? launched, string label)
    {
        if (launched == null) return null;
        using (launched)
        {
            // Only claim a new top-level window owned by the exact process we started.
            // Single-instance forwarding, existing windows and brokered launches remain unclaimed.
            try
            {
                var pid = launched.Id; var started = launched.StartTime.ToUniversalTime().Ticks;
                for (var i = 0; i < 20; i++)
                {
                    if (launched.HasExited) return null;
                    var candidates = VisibleWindows().Where(h => !before.Contains(h) && Pid(h) == pid && Native.Title(h).Length > 0).ToArray();
                    if (candidates.Length == 1)
                    {
                        var owned = new OwnedWindow(candidates[0], pid, started, label);
                        owned.destroyCallback = (_, _, h, objectId, childId, _, _) => { if (h == owned.Handle && objectId == 0 && childId == 0) owned.destroyed = true; };
                        owned.callbackRoot = GCHandle.Alloc(owned.destroyCallback);
                        owned.destroyHook = SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, owned.destroyCallback, (uint)pid, 0, 0);
                        if (owned.destroyHook == IntPtr.Zero) { owned.Dispose(); return null; }
                        if (!owned.IsSameWindow()) { owned.Dispose(); return null; }
                        return owned;
                    }
                    if (candidates.Length > 1) return null;
                    await Task.Delay(100);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
        }
        return null;
    }
    static int Pid(IntPtr handle) { Native.GetWindowThreadProcessId(handle, out var id); return (int)id; }
    bool IsSameWindow()
    {
        if (destroyed || released || !Native.IsWindow(Handle) || Pid(Handle) != ProcessId) return false;
        try { using var p = Process.GetProcessById(ProcessId); return p.StartTime.ToUniversalTime().Ticks == ProcessStarted; }
        catch { return false; }
    }
    public async Task<string> CloseAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsSameWindow()) { Dispose(); return "비서가 연 창이 이미 닫혔거나 추적이 끝나 추가로 닫지 않았어요."; }
        Native.ShowWindow(Handle, 9); Native.SetForegroundWindow(Handle);
        if (!PostMessage(Handle, 0x0010, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("창 닫기를 요청하지 못했어요.");
        // Sending WM_CLOSE lets the application present its own save dialog. Never terminate it.
        for (var i = 0; i < 10; i++) { await Task.Delay(100); if (!IsSameWindow()) { Dispose(); return $"비서가 연 ‘{Label}’ 창을 닫았어요."; } }
        throw new InvalidOperationException("창에 닫기를 요청했어요. 저장 확인이나 열린 대화상자를 직접 처리한 뒤 다시 말해 주세요.");
    }
    public void Dispose() { released = true; if (destroyHook != IntPtr.Zero) { UnhookWinEvent(destroyHook); destroyHook = IntPtr.Zero; } if (callbackRoot.IsAllocated) callbackRoot.Free(); destroyCallback = null; }
}
