using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    async Task<int> RunMultilingualAppChecksAsync()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); microphonePause.Enabled = false;
        var rows = new List<object>(); int failed = 0, requests = 0, brainCalls = 0, sequence = 0, status = 200;
        var reply = "Jarvis, tell me the colour of a cloud"; var probability = .99; string heardLanguage = "", brainPrompt = "";
        TaskCompletionSource? entered = null, release = null;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(x => x.Listen(IPAddress.Loopback, 0));
        await using var server = builder.Build();
        server.MapPost("/inference", async (HttpContext context) =>
        {
            Interlocked.Increment(ref requests); var form = await context.Request.ReadFormAsync(context.RequestAborted); heardLanguage = form["language"].ToString();
            var result = reply; var confidence = probability; var code = status; var pending = release; entered?.TrySetResult();
            if (pending != null) { try { await pending.Task.WaitAsync(context.RequestAborted); } catch (OperationCanceledException) { return; } }
            context.Response.StatusCode = code;
            await context.Response.WriteAsJsonAsync(new { text = result, segments = new[] { new { words = new[] { new { word = result, probability = confidence } } } } }, context.RequestAborted);
        });
        server.MapPost("/api/chat", async (HttpContext context) =>
        {
            Interlocked.Increment(ref brainCalls);
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            brainPrompt = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            await context.Response.WriteAsJsonAsync(new { message = new { content = "{\"reply\":\"Clouds can be white.\",\"actions\":[]}" } }, context.RequestAborted);
        });
        server.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            Interlocked.Increment(ref brainCalls);
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            brainPrompt = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            await context.Response.WriteAsJsonAsync(new { choices = new[] { new { message = new { role = "assistant", content = "Clouds can be white." } } } }, context.RequestAborted);
        });
        await server.StartAsync(); var url = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Hidden() => Require(!window.IsVisible && !overlay.IsVisible && PresentationSource.FromVisual(window) == null && PresentationSource.FromVisual(overlay) == null && !input.Running && WhisperEngine.Shared.ProcessId == 0, "A window microphone or managed engine was started");
        void Reset(string activation = "voice")
        {
            CancelWakeCheck(); StopTurn();
            settings = new UiSettings { Language = "en", Name = "Jarvis", Speak = false, Activation = activation, WhisperMode = "external", WhisperUrl = url, BrainProvider = "ollama", BrainModel = "fixture", BrainUrl = url, Desktop = new() { SetupComplete = true, StartupListening = "off" } };
            window.LoadSettings(settings); listening = true; starting = busy = speaking = dictating = holding = false;
            awakeUntil = DateTimeOffset.MinValue; speechCapturePending = waitingBarge = false; speechTailUntil = DateTimeOffset.MinValue;
            probability = .99; status = 200;
        }
        Task Final()
        {
            OnSpeech(JsonSerializer.SerializeToElement(new { type = "final", needs_transcription = true, text = "5분 타이머", command = "5분 타이머", has_wake = true, utterance = ++sequence, duration = 2.5, wav = Convert.ToBase64String(VoiceChecks.Silence()) }));
            return languageRecognitionTask;
        }
        async Task Check(string name, Func<Task> work)
        {
            try { await work(); Hidden(); rows.Add(new { name, pass = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); }
            finally { release?.TrySetResult(); release = entered = null; }
        }
        try
        {
            await Check("Hidden language settings preserve drafts backups overrides and microphone state", () =>
            {
                Reset(); settings = settings with { Language = "ko", Kws = true, WakeHints = true }; SettingsStore.Save(settings); window.LoadSettings(settings);
                window.SettingsSaved += next => { SettingsStore.Save(next); settings = next; window.LoadSettings(next); return null; };
                var language = (ComboBox)window.FindName("LanguageInput"); var kws = (CheckBox)window.FindName("KwsInput");
                var before = File.ReadAllText(AppPaths.ConfigFile); language.SelectedValue = "ja";
                Require(SettingsStore.Load().Language == "ko" && !kws.IsEnabled && kws.IsChecked == true, "Draft changed live setting or lost Korean options");
                window.SaveSettingsForCheck();
                Require(settings.Language == "ja" && SettingsStore.Load().Language == "ja" && File.ReadAllText(AppPaths.ConfigFile + ".backup") == before, "Language save or backup failed");
                language.SelectedValue = "ko"; Require(kws.IsEnabled && kws.IsChecked == true, "Korean options did not recover");
                return Task.CompletedTask;
            });
            await Check("Actual primary speech to AI response uses the selected language and exactly one transcription", async () =>
            {
                Reset(); reply = "Jarvis, tell me the colour of a cloud"; var calls = brainCalls; var audioCalls = requests; var timersBefore = timers.Items.Count;
                await Final().WaitAsync(TimeSpan.FromSeconds(8));
                Require(heardLanguage == "en" && brainCalls == calls + 1 && requests == audioCalls + 1 && brainPrompt.Contains("tell me the colour of a cloud") && brainPrompt.Contains("briefly in English"), "Primary transcript or language failed to reach Brain");
                Require(timers.Items.Count == timersBefore && !busy && languageRecognition == null, "Unverified worker text executed or processing remained busy");
            });
            await Check("Background speech uncertain words subtitles and HTTP failures cannot execute raw worker text", async () =>
            {
                var calls = brainCalls; var timersBefore = timers.Items.Count;
                foreach (var text in new[] { "tell me the colour of a cloud", "Thanks for watching", "Jarvis, tell me the colour of a cloud" })
                {
                    Reset(); reply = text; if (text.StartsWith("Jarvis")) probability = .1;
                    await Final().WaitAsync(TimeSpan.FromSeconds(5)); Require(!busy, "Rejected audio remained busy");
                }
                Reset(); reply = "Jarvis 5분 타이머"; status = 500; await Final().WaitAsync(TimeSpan.FromSeconds(5));
                Require(brainCalls == calls && timers.Items.Count == timersBefore && listening && !busy, "Unconfirmed audio reached an action");
            });
            await Check("Hotkey follow-up and hold release retain the explicit session during transcription", async () =>
            {
                foreach (var activation in new[] { "hotkey", "hold" })
                {
                    Reset(activation); reply = "tell me the colour of a cloud"; awakeUntil = DateTimeOffset.Now.AddSeconds(30); holding = activation == "hold";
                    var calls = brainCalls; var pending = Final(); OnSpeech(JsonSerializer.SerializeToElement(new { type = "flushed" }));
                    await pending.WaitAsync(TimeSpan.FromSeconds(5)); Require(brainCalls == calls + 1 && !holding && !busy, "Explicit speech session lost at release");
                }
            });
            await Check("Stopping input cancels pending primary inference and late replies cannot act", async () =>
            {
                Reset(); reply = "Jarvis 5분 타이머"; entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var count = timers.Items.Count; var pending = Final(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                StopInput(); release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!listening && !busy && languageRecognition == null && timers.Items.Count == count, "Late cancelled speech acted or retained busy state");
            });
            await Check("Settings changes and stale utterances reject pending results without replay", async () =>
            {
                Reset(); reply = "Jarvis 5분 타이머"; entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var count = timers.Items.Count; var pending = Final(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                settings = settings with { Language = "de" }; release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!busy && timers.Items.Count == count, "Old language result acted");
                var calls = requests; speechCapturePending = false;
                OnSpeech(JsonSerializer.SerializeToElement(new { type = "final", needs_transcription = true, utterance = sequence, wav = Convert.ToBase64String(VoiceChecks.Silence()) }));
                Require(calls == requests, "Stale utterance replayed");
            });
            await Check("Actual language section renders offscreen and never creates a presentation source", () =>
            {
                Reset(); var section = (FrameworkElement)window.FindName("ConversationLanguageSection");
                section.Measure(new Size(650, 400)); section.Arrange(new Rect(0, 0, 650, section.DesiredSize.Height)); section.UpdateLayout();
                var bitmap = new RenderTargetBitmap(650, Math.Max(1, (int)Math.Ceiling(section.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen()) { var rect = new Rect(0, 0, 650, section.ActualHeight); context.DrawRectangle(Brushes.White, null, rect); context.DrawRectangle(new VisualBrush(section), null, rect); } bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(AppPaths.Home, "language-settings.png"); using var stream = File.Create(path); encoder.Save(stream);
                Require(new FileInfo(path).Length > 1000, "Empty language render"); return Task.CompletedTask;
            });
            await Check("Missing multilingual model fails during preparation before opening the microphone", async () =>
            {
                Reset(); listening = false; settings = settings with { WhisperMode = "cpu" };
                StartListening(); await languageStartupTask.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!listening && !starting && languageStartup == null && !input.Running && WhisperEngine.Shared.ProcessId == 0, "Missing model started input or retained startup");
            });
        }
        finally
        {
            CancelWakeCheck(); release?.TrySetResult(); await server.StopAsync(); input.Dispose(); window.Close(); overlay.Close();
            var path = Path.Combine(AppPaths.Home, "multilingual-app-checks.json"); File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, microphoneCapture = false, speakerPlayback = false, visibleWindows = false, checks = rows }, SettingsStore.Json));
        }
        return failed == 0 ? 0 : 1;
    }
}
