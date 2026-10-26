using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceAssistant.Desktop;

internal static class MicrophoneChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task<int> RunAsync(List<object> rows, bool native)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Microphone pause excludes only exact own PIDs and retains unknown or same-name peers", () =>
        {
            var now = DateTimeOffset.UtcNow; var gate = new MicrophonePause(() => now);
            gate.Observe(new(true, [new(10, "VoiceAssistant"), new(11, "python"), new(12, "python"), new(12, "python"), new(0, "unknown")], 1, now), 10, 11, 0);
            Require(gate.Users.Select(x => x.ProcessId).SequenceEqual(new[] { 12, 0 }) && gate.Blocked && !gate.Permit(true), "Peer ignored or own process included");
            gate.Observe(new(true, [new(10, "VoiceAssistant"), new(11, "python")], 1, now), 10, 11);
            Require(!gate.Blocked && gate.Permit(true) && !gate.Permit(false), "Own capture causes pause or stopped mic resumes");
            return Task.CompletedTask;
        });
        await Check("Microphone pause does not assume unknown failed or stale capture state is clear", () =>
        {
            var now = DateTimeOffset.UtcNow; var gate = new MicrophonePause(() => now);
            Require(gate.Blocked, "Initial status assumed clear");
            gate.Observe(new(true, [], 1, now)); Require(!gate.Blocked, "Fresh empty state blocked");
            now = now.AddSeconds(6); Require(gate.Blocked && !gate.Known, "Stale state allowed");
            gate.Observe(new(false, [], 0, now)); Require(gate.Blocked, "Failed read assumed clear");
            return Task.CompletedTask;
        });
        await Check("Manual microphone command can run during a call then returns to automatic pause", () =>
        {
            var now = DateTimeOffset.UtcNow; var gate = new MicrophonePause(() => now);
            gate.Observe(new(true, [new(25, "call")], 1, now));
            gate.BeginManual(TimeSpan.FromSeconds(3)); Require(gate.AutomaticPaused && !gate.Blocked && gate.Permit(true), "Hotkey unavailable");
            gate.EndManual(); Require(gate.Blocked, "Command completion did not re-pause");
            gate.BeginManual(TimeSpan.FromSeconds(3)); now = now.AddSeconds(3);
            Require(gate.Blocked && !gate.Manual, "Silent command did not expire");
            gate.BeginManual(TimeSpan.FromSeconds(3)); Require(!gate.Permit(false), "Manual allowance turned listening on");
            return Task.CompletedTask;
        });
        await Check("Capture end resumes enabled listening and preference can disable automatic pause", () =>
        {
            var now = DateTimeOffset.UtcNow; var gate = new MicrophonePause(() => now);
            gate.Observe(new(true, [new(25, "call")], 1, now));
            gate.Enabled = false; Require(!gate.Blocked && gate.Permit(true) && !gate.Permit(false), "Preference ignored");
            gate.Enabled = true; Require(gate.Blocked, "Re-enabled preference ignored");
            gate.Observe(new(true, [], 1, now)); Require(!gate.Blocked && gate.Permit(true) && !gate.Permit(false), "Capture end lost listening intent");
            return Task.CompletedTask;
        });
        await Check("Microphone pause defaults on for old settings and survives export import review", () =>
        {
            var old = JsonSerializer.Deserialize<UiSettings>("{}")!;
            Require(old.PauseInCalls, "Old settings default changed");
            var imported = SettingsTransfer.Parse(SettingsTransfer.Serialize(old with { PauseInCalls = false }));
            var restored = SettingsTransfer.Prepare(imported, old, false, false);
            Require(!restored.PauseInCalls && SettingsTransfer.Describe(old, restored).Contains("마이크"), "Preference lost or hidden from review");
            return Task.CompletedTask;
        });
        await Check("Unstarted microphone monitor disposes idempotently and cannot restart", () =>
        {
            var monitor = new MicrophoneMonitor(); monitor.Dispose(); monitor.Dispose();
            try { monitor.Start(); } catch (ObjectDisposedException) { return Task.CompletedTask; }
            throw new Exception("Disposed monitor started");
        });
        if (native) await Check("Native capture sessions observe a new peer, exclude self, and notice peer exit", NativeAsync);
        return failed;
    }

    // Explicit hardware-only fixture. Samples are discarded, never transcribed or written to disk.
    internal static Task<int> RunPeerAsync(string[] args) => Task.Run(async () =>
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var seconds) || seconds < 2 || seconds > 120) return 2;
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            using var recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().Build();
            recorder.StartRecording(); await Task.Delay(TimeSpan.FromSeconds(seconds)); recorder.StopRecording(); return 0;
        }
        catch (Exception ex) { AppPaths.Log("microphone-peer", ex.GetType().Name + ": " + ex.Message); return 1; }
    });

    static async Task NativeAsync()
    {
        MicrophoneSnapshot? latest = null; var sync = new object();
        using var monitor = new MicrophoneMonitor();
        monitor.Changed += value => { lock (sync) latest = value; };
        monitor.Start();
        async Task<MicrophoneSnapshot> Wait(Func<MicrophoneSnapshot, bool> matches)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(12))
            {
                MicrophoneSnapshot? value; lock (sync) value = latest;
                if (value is { Available: true } && matches(value)) return value;
                await Task.Delay(100);
            }
            throw new TimeoutException("Capture session transition not observed");
        }
        await Wait(_ => true);
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        using var recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().Build();
        recorder.StartRecording();
        var own = await Wait(x => x.Users.Any(u => u.ProcessId == Environment.ProcessId));
        var gate = new MicrophonePause(); gate.Observe(own, Environment.ProcessId);
        Require(gate.Users.All(x => x.ProcessId != Environment.ProcessId), "Self remained in capture users");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--microphone-peer"); info.ArgumentList.Add("8");
        using var peer = Process.Start(info) ?? throw new Exception("Peer did not start");
        try
        {
            var active = await Wait(x => x.Users.Any(u => u.ProcessId == peer.Id));
            gate.Observe(active, Environment.ProcessId);
            Require(gate.Blocked && gate.Users.Any(x => x.ProcessId == peer.Id), "External capture not paused");
            await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Require(peer.ExitCode == 0, "Peer capture failed");
            var ended = await Wait(x => x.Users.All(u => u.ProcessId != peer.Id));
            gate.Observe(ended, Environment.ProcessId);
            Require(gate.Users.All(x => x.ProcessId != peer.Id), "Ended peer retained");
        }
        finally { if (!peer.HasExited) { peer.Kill(); await peer.WaitForExitAsync(); } recorder.StopRecording(); }
    }
    internal static async Task<int> SnapshotAsync()
    {
        MicrophoneSnapshot? latest = null;
        using var monitor = new MicrophoneMonitor();
        monitor.Changed += value => System.Threading.Volatile.Write(ref latest, value);
        monitor.Start(); await Task.Delay(2500);
        var result = System.Threading.Volatile.Read(ref latest);
        Console.WriteLine(JsonSerializer.Serialize(result, SettingsStore.Json));
        return result?.Available == true ? 0 : 1;
    }
}
