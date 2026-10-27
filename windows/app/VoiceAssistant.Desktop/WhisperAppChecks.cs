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
        int failed = 0, requests = 0; string refined = "알파카 5분 타이머"; bool wait = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new List<object>();
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var server = builder.Build();
        server.MapPost("/inference", async (HttpContext context) =>
        {
            Interlocked.Increment(ref requests); _ = await context.Request.ReadFormAsync(context.RequestAborted);
            if (wait) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            await context.Response.WriteAsJsonAsync(new { text = refined }, context.RequestAborted);
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
                    body.Measure(new Size(610, 455)); body.Arrange(new Rect(0, 0, 610, 455)); body.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(610, 455, 96, 96, PixelFormats.Pbgra32); bitmap.Render(body);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(AppPaths.WindowsRoot, "artifacts", "whisper-setup.png")); png.Save(file);
                    choice.SelectedValue = "cpu"; Require(!apply.IsEnabled && dialog.SelectedMode == null, "Selection applies without confirmation");
                }
                finally { dialog.Close(); }
                return Task.CompletedTask;
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
