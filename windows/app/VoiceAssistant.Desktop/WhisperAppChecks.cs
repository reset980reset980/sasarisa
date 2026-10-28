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
    async Task<int> RunWhisperAppChecksAsync()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); window.SettingsSaved += SaveSettings;
        int failed = 0, requests = 0; string refined = "알파카 5분 타이머"; bool wait = false; string? rawResponse = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new List<object>();
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        string Reply(string text, params WhisperWord[] words) => JsonSerializer.Serialize(new { text, segments = new[] { new { words = words.Select(w => new { word = w.Text, probability = w.Probability }).ToArray() } } });
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var server = builder.Build();
        server.MapPost("/inference", async (HttpContext context) =>
        {
            Interlocked.Increment(ref requests); var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form["response_format"] != "verbose_json" || form["token_timestamps"] != "false" || form["no_language_probabilities"] != "true" || form["language"] != "ko" || form.Files.Count != 1) { context.Response.StatusCode = 400; return; }
            if (wait) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            if (rawResponse != null) { context.Response.ContentType = "application/json"; await context.Response.WriteAsync(rawResponse, context.RequestAborted); }
            else await context.Response.WriteAsJsonAsync(new { text = refined }, context.RequestAborted);
        });
        await server.StartAsync(); var url = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var wav = Convert.ToBase64String(new byte[44]);
        try
        {
            await Check("Whisper actual WPF settings keep local engine as a draft until Save", () =>
            {
                settings = settings with { WhisperUrl = url, WhisperMode = "external", Speak = false }; SettingsStore.Save(settings); window.LoadSettings(settings);
                window.SelectWhisperMode("cuda"); Require(SettingsStore.Load().WhisperMode == "external", "Draft saved early");
                window.SaveSettingsForCheck(); Require(settings.WhisperMode == "cuda" && SettingsStore.Load().WhisperMode == "cuda", "Managed preference not saved");
                Require(WhisperEngine.Shared.ProcessId == 0 && !input.Running, "Settings started microphone or engine");
                window.SelectWhisperMode("external"); window.SaveSettingsForCheck();
                Require(settings.WhisperUrl == url && !settings.Speak && File.Exists(AppPaths.ConfigFile + ".backup"), "Other settings or backup lost"); return Task.CompletedTask;
            });
            await Check("Whisper setup window renders and refuses Apply until engine verification", () =>
            {
                var dialog = new WhisperSetupWindow("cuda");
                try
                {
                    var body = (FrameworkElement)dialog.Content;
                    var choice = VocabularyControls<ComboBox>(body).Single();
                    Require((string)choice.SelectedValue == "cuda", "Selected GPU mode lost");
                    var apply = VocabularyControls<Button>(body).Single(x => Equals(x.Content, "설정에 선택"));
                    Require(!apply.IsEnabled, "Unverified engine marked ready");
                    body.Measure(new Size(640, 610)); body.Arrange(new Rect(0, 0, 640, 610)); body.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(640, 610, 96, 96, PixelFormats.Pbgra32); bitmap.Render(body);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-setup.png")); png.Save(file);
                    choice.SelectedValue = "cpu"; Require(!apply.IsEnabled && dialog.SelectedMode == null, "Selection applies without confirmation");
                }
                finally { dialog.Close(); }
                return Task.CompletedTask;
            });
            await Check("Whisper WPF runtime repair handles missing success cancellation and failures without applying settings", async () =>
            {
                bool ready = false, fail = false, waiting = false; int launches = 0;
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var dialog = new WhisperSetupWindow("cpu", () => new(ready, ready ? "구성 요소 있음" : "Microsoft 구성 요소 없음"),
                    (_, token) => Task.FromResult("verified-test-file"), async (_, token) => { launches++; if (fail) throw new IOException("시험 설치 실패"); if (waiting) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } ready = true; return 3010; });
                try
                {
                    var body = (FrameworkElement)dialog.Content;
                    var buttons = VocabularyControls<Button>(body).ToArray();
                    var repair = buttons.Single(x => Equals(x.Content, "Microsoft 구성 요소 설치·복구"));
                    var apply = buttons.Single(x => Equals(x.Content, "설정에 선택"));
                    Require(!apply.IsEnabled && launches == 0, "Opening setup started an installer");
                    repair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield();
                    Require(launches == 1 && ready && repair.IsEnabled && !apply.IsEnabled && dialog.SelectedMode == null, "Runtime install silently enabled engine");
                    Require(VocabularyControls<TextBlock>(body).Any(x => x.Text.Contains("Windows를 다시 시작해야")), "Reboot-required outcome lost");
                    ready = false; fail = true; repair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Yield();
                    Require(repair.IsEnabled && VocabularyControls<TextBlock>(body).Any(x => x.Text.Contains("시험 설치 실패")), "Installer failure hidden");
                    fail = false; waiting = true; repair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    buttons.Single(x => Equals(x.Content, "대기 중지")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (int n = 0; n < 50 && !repair.IsEnabled; n++) await Task.Delay(20);
                    Require(repair.IsEnabled && !apply.IsEnabled && !ready && dialog.SelectedMode == null, "Cancelled installer wait applies settings or leaves UI busy");
                }
                finally { dialog.Close(); }
            });
            await Check("Whisper App skips known clock commands and typed input while refining speech with custom text", async () =>
            {
                await SubmitAsync("지금 몇 시야", true, wav); Require(requests == 0, "Clock reached slow recognizer");
                await SubmitAsync("5분 타이머", false, wav); Require(requests == 0, "Typed text sent as audio");
                int before = timers.Items.Count;
                await SubmitAsync("알파가 5분 타이머", true, wav);
                Require(requests == 1 && timers.Items.Count == before + 1, "Refined command did not execute");
                Require(File.ReadAllText(Path.Combine(AppPaths.Home, "assistant.log")).Contains("알파카 5분 타이머"), "Corrected text not used");
            });
            await Check("Whisper App rejects hallucinated commands and uses live recognized timer", async () =>
            {
                refined = "Thanks for watching"; int before = timers.Items.Count;
                await SubmitAsync("5분 타이머", true, wav);
                Require(requests == 2 && timers.Items.Count == before + 1, "Live command lost after hallucination");
                refined = "알파카 타임업 시작해"; before = timers.Items.Count;
                await SubmitAsync("알파카 5분 타이머", true, wav);
                Require(timers.Items.Count == before + 1, "Broken refinement overrode a known live command");
            });
            await Check("Whisper App uncertain word pieces neither replace live commands nor learn vocabulary", async () =>
            {
                settings = settings with { Text = settings.Text with { AutoLearnWords = true } }; SettingsStore.Save(settings);
                var before = JsonSerializer.Serialize(settings.Text); var disk = File.ReadAllText(AppPaths.ConfigFile);
                rawResponse = Reply(settings.Name + " 블렌더 5분 타이머", new(" " + settings.Name, 0.9), new(" 블렌", 0.9), new("더", 0.2), new(" 5분", 0.95), new(" 타이머", 0.9));
                for (int i = 0; i < 3; i++)
                {
                    int count = timers.Items.Count; await SubmitAsync("블렌다 5분 타이머", true, wav);
                    Require(timers.Items.Count == count + 1 && timers.Items[^1].Label.Contains("블렌다"), "Uncertain term overrode live command");
                }
                Require(JsonSerializer.Serialize(settings.Text) == before && File.ReadAllText(AppPaths.ConfigFile) == disk, "Uncertain word was learned or persisted");
                rawResponse = rawResponse.Replace("0.2", "0.8");
                await SubmitAsync("블렌다 5분 타이머", true, wav);
                Require(timers.Items[^1].Label.Contains("블렌더") && settings.Text.WordCandidates.Values.Any(x => x.Word == "블렌더" && x.Count == 1), "Certain correction did not reach command and learning");
                rawResponse = null;
            });
            await Check("Whisper App low confidence number cannot change the requested timer duration", async () =>
            {
                rawResponse = Reply("삼십 분 타이머", new(" 삼", 0.9), new("십", 0.1), new(" 분", 0.9), new(" 타이머", 0.9));
                await SubmitAsync("십 분 타이머", true, wav);
                var remaining = (timers.Items[^1].Due - DateTimeOffset.Now).TotalSeconds;
                Require(remaining is > 590 and <= 601, "Low-confidence number changed ten minutes to thirty"); rawResponse = null;
            });
            await Check("Whisper App malformed probabilities and oversized HTTP replies fall back without learning", async () =>
            {
                var before = JsonSerializer.Serialize(settings.Text);
                foreach (var body in new[] { "{\"text\":\"블렌더 5분 타이머\",\"segments\":[{\"words\":[{\"word\":\" 블렌더\",\"probability\":\"invalid\"}]}]}", new string(' ', 1_048_577) })
                {
                    rawResponse = body; int count = timers.Items.Count; await SubmitAsync("블렌다 5분 타이머", true, wav);
                    Require(timers.Items.Count == count + 1 && timers.Items[^1].Label.Contains("블렌다"), "Invalid reply lost or replaced live command");
                    Require(JsonSerializer.Serialize(settings.Text) == before, "Invalid reply learned words");
                }
                rawResponse = null;
            });
            await Check("Whisper App cancellation runs no action and missing managed engine falls back", async () =>
            {
                wait = true; int before = timers.Items.Count;
                var pending = SubmitAsync("5분 타이머", true, wav);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); StopTurn(); await pending;
                Require(timers.Items.Count == before, "Cancelled recognition ran a command");
                wait = false; settings = settings with { WhisperMode = "cuda" };
                await SubmitAsync("5분 타이머", true, wav);
                Require(timers.Items.Count == before + 1 && WhisperEngine.Shared.ProcessId == 0, "Missing engine prevents basic command or leaked process");
                Require(!input.Running, "Checks activated microphone");
            });
        }
        finally { await server.StopAsync(); timers.Clear(); StopInput(); StopTurn(); overlay.Close(); window.Close(); }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-app-checks.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
}
