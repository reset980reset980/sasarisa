using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VoiceAssistant.Desktop;

// Capture-endpoint sessions only. No audio buffers, playback sessions, permissions or registry edits.
public sealed class MicrophoneMonitor : IDisposable
{
    readonly ManualResetEvent stop = new(false);
    readonly Thread thread;
    readonly object lifetime = new();
    bool started;
    int disposed;
    public event Action<MicrophoneSnapshot>? Changed;
    public MicrophoneMonitor()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Microphone session monitor" };
        thread.SetApartmentState(ApartmentState.MTA);
    }
    public void Start()
    {
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (started) return;
            started = true; thread.Start();
        }
    }
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    sealed class Endpoint : IDisposable
    {
        readonly MMDevice device;
        readonly AudioSessionManager manager;
        readonly ConcurrentQueue<AudioSessionControl> created = new();
        readonly Dictionary<string, AudioSessionControl> sessions = new();
        readonly object callbackGate = new();
        bool closed;
        public Endpoint(MMDevice device)
        {
            this.device = device;
            manager = device.AudioSessionManager;
            manager.OnSessionCreated += SessionCreated;
        }
        void SessionCreated(object sender, AudioSessionControl session)
        {
            lock (callbackGate) { if (closed) session.Dispose(); else created.Enqueue(session); }
        }
        void Add(AudioSessionControl session)
        {
            try { var key = session.GetSessionInstanceIdentifier; if (sessions.TryAdd(key, session)) return; }
            catch { session.Dispose(); throw; }
            session.Dispose();
        }
        public void Read(List<MicrophoneUser> users)
        {
            // Keep notification references as well as a fresh enumeration. Enumeration alone can
            // miss a just-created session; polling also catches device and session state changes.
            manager.RefreshSessions(); var current = manager.Sessions;
            for (var i = 0; i < current.Count; i++) Add(current[i]);
            while (created.TryDequeue(out var added)) Add(added);
            foreach (var (key, session) in sessions.ToArray())
            {
                var state = session.State;
                if (state == AudioSessionState.AudioSessionStateExpired) { sessions.Remove(key); session.Dispose(); continue; }
                if (state != AudioSessionState.AudioSessionStateActive) continue;
                var pid = checked((int)session.GetProcessID); var name = "다른 앱";
                try { using var process = Process.GetProcessById(pid); name = process.ProcessName; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                users.Add(new(pid, name));
            }
        }
        public void Dispose()
        {
            lock (callbackGate) { closed = true; manager.OnSessionCreated -= SessionCreated; }
            while (created.TryDequeue(out var item)) item.Dispose();
            foreach (var item in sessions.Values) item.Dispose(); sessions.Clear();
            device.Dispose(); // also owns and disposes its session manager
        }
    }
    void Run()
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
        var endpoints = new Dictionary<string, Endpoint>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            while (!stop.WaitOne(0))
            {
                var users = new List<MicrophoneUser>(); var available = true; var seen = new HashSet<string>();
                try
                {
                    using var list = devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    foreach (var device in list)
                    {
                        var retained = false;
                        try
                        {
                            var id = device.ID; seen.Add(id);
                            if (!endpoints.TryGetValue(id, out var endpoint)) { endpoint = new Endpoint(device); endpoints.Add(id, endpoint); retained = true; }
                            try { endpoint.Read(users); }
                            catch (Exception ex) when (ex is COMException or CoreAudioException or InvalidOperationException)
                            { available = false; endpoint.Dispose(); endpoints.Remove(id); }
                        }
                        finally { if (!retained) device.Dispose(); }
                    }
                    foreach (var id in endpoints.Keys.Where(x => !seen.Contains(x)).ToArray()) { endpoints[id].Dispose(); endpoints.Remove(id); }
                }
                catch (Exception ex) when (ex is COMException or CoreAudioException or InvalidOperationException)
                { available = false; }
                if (Volatile.Read(ref disposed) == 0) Changed?.Invoke(new(available, users.DistinctBy(x => x.ProcessId).ToArray(), seen.Count, DateTimeOffset.UtcNow));
                if (stop.WaitOne(1000)) break;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("microphone-monitor", ex.GetType().Name + ": " + ex.Message);
            if (Volatile.Read(ref disposed) == 0) Changed?.Invoke(new(false, Array.Empty<MicrophoneUser>(), 0, DateTimeOffset.UtcNow));
        }
        finally
        {
            foreach (var endpoint in endpoints.Values) { try { endpoint.Dispose(); } catch { } }
            if (initialized) CoUninitialize();
            // The worker owns the wait handle, including when a slow COM call outlives Dispose.
            stop.Dispose();
        }
    }
    public void Dispose()
    {
        lock (lifetime)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { stop.Set(); } catch (ObjectDisposedException) { }
            if (!started) { stop.Dispose(); return; }
        }
        if (Thread.CurrentThread != thread) thread.Join(1500);
    }
}
