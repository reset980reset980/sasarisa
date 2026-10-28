using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.Desktop;

public partial class App
{
    async Task<int> RunWhisperWakeAppChecksAsync()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); microphonePause.Enabled = false;
        int failed = 0, requests = 0, sequence = 0, status = 200;
        string reply = "사사리사", hint = "";
        TaskCompletionSource? release = null, entered = null;
        var rows = new List<object>();
        void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } finally { release?.TrySetResult(); release = null; status = 200; } }
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var server = builder.Build();
        server.MapPost("/inference", async (HttpContext context) =>
        {
            Interlocked.Increment(ref requests); var form = await context.Request.ReadFormAsync(context.RequestAborted); hint = form["prompt"].ToString();
            var answer = reply; var resultStatus = status; var held = release;
            entered?.TrySetResult(); if (held != null) await held.Task; // deliberately allow the server to finish after client cancellation
            context.Response.StatusCode = resultStatus;
            if (resultStatus == 200) await context.Response.WriteAsJsonAsync(new { text = answer });
        });
        await server.StartAsync(); var url = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var wav = Convert.ToBase64String(new byte[44]);
        void Idle()
        {
            CancelWakeCheck(); settings = new UiSettings { Name = "Sasarisa", Activation = "voice", WhisperMode = "external", WhisperUrl = url, Speak = false };
            listening = true; starting = busy = speaking = dictating = holding = false; speechCapturePending = waitingBarge = false;
            awakeUntil = speechTailUntil = DateTimeOffset.MinValue; microphonePause.EndManual(); microphonePause.Enabled = false;
        }
        void Message(object value) { using var json = JsonDocument.Parse(JsonSerializer.Serialize(value)); OnSpeech(json.RootElement); }
        void Final(string text = "사서리사", double duration = 1.2) => Message(new { type = "final", text, command = text, has_wake = false, utterance = ++sequence, duration, wav });
        async Task Pending()
        {
            release = new(TaskCreationOptions.RunContinuationsAsynchronously); entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Final("사서리사 오분 타이머"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        try
        {
            await Check("Whisper wake actual App accepts a missed name and runs its verified command once", async () =>
            {
                Idle(); reply = "사사리사 오분 타이머"; int count = timers.Items.Count, calls = requests;
                Final("사서리사 오분 타이머"); await wakeCheckTask.WaitAsync(TimeSpan.FromSeconds(5));
                Require(timers.Items.Count == count + 1 && requests == calls + 1 && awakeUntil > DateTimeOffset.Now, "Name/command lost or re-transcribed");
                Require(hint.Contains("Sasarisa") && hint.Contains("사사리사") && !input.Running, "Prompt missing or microphone opened");
            });
            await Check("Whisper wake rejected command only opens the follow-up listening window", async () =>
            {
                Idle(); reply = "사사리사 삼십분 타이머"; int count = timers.Items.Count, capture = input.CaptureRequest;
                Final("서서 있어"); await wakeCheckTask.WaitAsync(TimeSpan.FromSeconds(5));
                Require(awakeUntil > DateTimeOffset.Now && timers.Items.Count == count && capture == input.CaptureRequest, "Rejected command ran or capture reset");
                Idle(); reply = "사사리사"; Final(""); await wakeCheckTask.WaitAsync(TimeSpan.FromSeconds(5));
                Require(awakeUntil > DateTimeOffset.Now && timers.Items.Count == count, "Empty live Latin name did not wake safely");
            });
            await Check("Whisper wake candidate gates and endpoint failure leave ordinary listening available", async () =>
            {
                Idle(); int calls = requests;
                foreach (var mode in new[] { "hold", "hotkey" }) { settings = settings with { Activation = mode }; Final(); }
                settings = settings with { Activation = "voice" }; Final("거기 있어"); Final("사서리사", 4.5);
                Require(requests == calls && !busy && listening, "Ineligible sound started inference");
                status = 500; Final(); await wakeCheckTask.WaitAsync(TimeSpan.FromSeconds(5));
                Require(listening && !busy && awakeUntil <= DateTimeOffset.Now && wakeCheck == null, "Failure interrupted ordinary listening");
            });
            await Check("Whisper wake new partial cancels old audio without stopping capture", async () =>
            {
                Idle(); reply = "사사리사 오분 타이머"; int count = timers.Items.Count, generation = input.Generation, capture = input.CaptureRequest;
                await Pending(); var pending = wakeCheckTask;
                Require(!busy && listening, "Idle check blocked ordinary listening");
                Message(new { type = "partial", text = "새로운 말", utterance = sequence + 1 });
                await pending.WaitAsync(TimeSpan.FromSeconds(3)); release!.TrySetResult();
                Require(timers.Items.Count == count && awakeUntil <= DateTimeOffset.Now && generation == input.Generation && capture == input.CaptureRequest, "Old audio acted or capture stopped");
            });
            await Check("Whisper wake stop manual wake capture stop and new typed command preempt the idle check", async () =>
            {
                foreach (var operation in new[] { "stop", "wake", "capture", "input", "command", "settings" })
                {
                    Idle(); reply = "사사리사 오분 타이머"; int count = timers.Items.Count;
                    await Pending(); var pending = wakeCheckTask;
                    switch (operation)
                    {
                        case "stop": StopTurn(); break;
                        case "wake": Wake(); break;
                        case "capture": CaptureInput(false); break;
                        case "input": StopInput(); break;
                        case "command": await SubmitAsync("십 분 타이머", false); break;
                        case "settings": listening = false; Require(SaveSettings(settings with { Name = "다른이름" }) == null, "Settings save failed"); break;
                    }
                    await pending.WaitAsync(TimeSpan.FromSeconds(3)); release!.TrySetResult(); release = null;
                    Require(timers.Items.Count == count + (operation == "command" ? 1 : 0) && wakeCheck == null && !input.Running, "Late command or microphone started: " + operation);
                }
            });
            await Check("Whisper wake late replies are fenced by generation capture utterance and microphone pause", async () =>
            {
                foreach (var change in new[] { "generation", "capture", "utterance", "pause", "speech", "settings" })
                {
                    Idle(); reply = "사사리사 오분 타이머"; int count = timers.Items.Count;
                    await Pending(); var pending = wakeCheckTask;
                    switch (change)
                    {
                        case "generation": input.Stop(); break;
                        case "capture": input.Capture(false); break;
                        case "utterance": lastUtterance++; sequence = lastUtterance; break;
                        case "pause": microphonePause.Enabled = true; break;
                        case "speech": speaking = true; break;
                        case "settings": settings = settings with { Name = "바뀐이름" }; break;
                    }
                    release!.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(3)); release = null;
                    Require(timers.Items.Count == count && awakeUntil <= DateTimeOffset.Now, "Stale response acted: " + change);
                }
            });
            await Check("Whisper wake skips occupied engines and a new normal recognition does not wait for cancelled idle HTTP", async () =>
            {
                Idle(); reply = "사사리사";
                release = new(TaskCreationOptions.RunContinuationsAsynchronously); entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancel = new CancellationTokenSource(); var command = WhisperEngine.Shared.RefineAsync(settings, wav, cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); int calls = requests;
                Require(await WhisperEngine.Shared.TryRefineAsync(settings, wav, default) == null && requests == calls, "Idle request queued behind command");
                cancel.Cancel(); try { await command; throw new Exception("Cancelled command completed"); } catch (OperationCanceledException) { }
                release.TrySetResult(); release = null;
                await Pending(); var idle = wakeCheckTask; CancelWakeCheck(); await idle.WaitAsync(TimeSpan.FromSeconds(3));
                var abandoned = release; release = null; reply = "오분 타이머";
                try
                {
                    var next = await WhisperEngine.Shared.RefineAsync(settings, wav, default).WaitAsync(TimeSpan.FromSeconds(3));
                    Require(next.Text == "오분 타이머", "Normal recognition remained behind idle request");
                }
                finally { abandoned!.TrySetResult(); }
            });
        }
        finally { release?.TrySetResult(); CancelWakeCheck(); await wakeCheckTask; await server.StopAsync(); listening = false; timers.Clear(); StopInput(); StopTurn(); overlay.Close(); window.Close(); }
        File.WriteAllText(Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-wake-app-checks.json"), JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json));
        return failed == 0 ? 0 : 1;
    }
}
