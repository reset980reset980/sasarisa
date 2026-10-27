using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    async Task<int> RunVocabularyAppChecksAsync()
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); window.SettingsSaved += SaveSettings;
        settings = settings with { Name = "단어시험", Speak = false }; window.LoadSettings(settings);
        int failed = 0, requests = 0, status = 200; bool wait = false, sawHint = false;
        string refined = "알파카 30분 타이머";
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new List<object>();
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.Message }); } }
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var server = builder.Build();
        server.MapPost("/inference", async (HttpContext context) =>
        {
            Interlocked.Increment(ref requests);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form["prompt"].ToString().Contains("알파카")) sawHint = true;
            if (form.Files.Count != 1 || form.Files[0].Length != 44 || form["language"] != "ko") { context.Response.StatusCode = 400; return; }
            if (wait) { waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            context.Response.StatusCode = status;
            if (status == 200) await context.Response.WriteAsJsonAsync(new { text = refined });
        });
        await server.StartAsync();
        var url = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        settings = settings with { WhisperUrl = url }; window.LoadSettings(settings);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.ASCII, true))
        { writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(0); }
        var wav = Convert.ToBase64String(bytes.ToArray());
        try
        {
            await Check("Actual WPF saves the opt-in for automatic vocabulary learning", () =>
            {
                ((CheckBox)window.FindName("AutoLearnWordsInput")).IsChecked = true; window.SaveSettingsForCheck();
                Require(settings.Text.AutoLearnWords && SettingsStore.Load().Text.AutoLearnWords, "Opt-in not saved"); return Task.CompletedTask;
            });
            await Check("Local inference HTTP fixture reaches actual App learning after three voice requests", async () =>
            {
                ((TextBox)window.FindName("NameInput")).Text = "저장 전 이름";
                ((TextBox)window.FindName("CustomWordsInput")).Text = "직접입력";
                for (int i = 0; i < 3; i++)
                {
                    await SubmitAsync("알파 30분 타이머", true, wav);
                    Require(settings.Text.CustomWords.Contains("알파카") == (i == 2), "Learning threshold wrong");
                }
                Require(requests == 3 && timers.Items.Count == 3 && SettingsStore.Load().Text.CustomWords.Contains("알파카"), "Refinement or execution path missing");
                Require(!VocabularyFiles.Read(AppPaths.ConfigFile + ".vocabulary-backup").Words.Contains("알파카"), "Pre-learning backup already contains new word");
            });
            await Check("Background vocabulary learning preserves unsaved settings and adds the new word to the draft", () =>
            {
                Require(((TextBox)window.FindName("NameInput")).Text == "저장 전 이름", "Unrelated edit lost");
                var draft = ((TextBox)window.FindName("CustomWordsInput")).Text;
                Require(draft.Contains("직접입력") && draft.Contains("알파카"), "Draft or learned word lost");
                window.SaveSettingsForCheck();
                Require(SettingsStore.Load().Text.CustomWords.Contains("알파카") && settings.Name == "저장 전 이름", "Later manual save erased learning"); return Task.CompletedTask;
            });
            await Check("Disabled learning typed input failed refinement and cancellation do not learn words", async () =>
            {
                var before = JsonSerializer.Serialize(settings.Text); var requestCount = requests;
                await SubmitAsync("30분 타이머", false, wav); Require(requests == requestCount && JsonSerializer.Serialize(settings.Text) == before, "Typed input reached learning");
                settings = settings with { Text = settings.Text with { AutoLearnWords = false } }; refined = "블렌더 30분 타이머";
                await SubmitAsync("블렌다 30분 타이머", true, wav); Require(settings.Text.WordCandidates.Count == 0, "Disabled learning stored a candidate");
                settings = settings with { Text = settings.Text with { AutoLearnWords = true } }; status = 500;
                await SubmitAsync("블렌다 30분 타이머", true, wav); Require(settings.Text.WordCandidates.Count == 0, "Failed refinement learned");
                status = 200; wait = true; var timersBefore = timers.Items.Count;
                var pending = SubmitAsync("블렌다 30분 타이머", true, wav);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); StopTurn(); await pending;
                Require(settings.Text.WordCandidates.Count == 0 && timers.Items.Count == timersBefore, "Cancelled refinement learned or executed");
                Require(sawHint, "Saved terms were not sent to the local recognizer prompt"); wait = false;
            });
            await Check("Failed vocabulary save leaves live settings disk settings and temporary files unchanged", () =>
            {
                var before = File.ReadAllText(AppPaths.ConfigFile); var live = JsonSerializer.Serialize(settings);
                using (var locked = new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read)) LearnVocabulary("블렌다", "블렌더");
                Require(File.ReadAllText(AppPaths.ConfigFile) == before && JsonSerializer.Serialize(settings) == live, "Failed save mutated settings");
                Require(Directory.GetFiles(AppPaths.Home, "config.json.*.tmp").Length == 0, "Save stage leaked"); return Task.CompletedTask;
            });
            await Check("Actual vocabulary review applies a backup only after confirmation and main settings save", () =>
            {
                var backup = VocabularyFiles.Read(AppPaths.ConfigFile + ".vocabulary-backup");
                var review = new VocabularyImportWindow(backup, settings.Text, restore: true) { ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
                Exception? failure = null;
                review.Loaded += (_,_) =>
                {
                    try { VocabularyControls<Button>(review).Single(x => Equals(x.Content, "목록에 적용")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    catch (Exception ex) { failure = ex; review.Close(); }
                };
                var result = review.ShowDialog(); if (failure != null) throw failure;
                Require(result == true && review.Result != null && settings.Text.CustomWords.Contains("알파카"), "Review changed saved settings too early");
                window.ApplyVocabularyDraft(review.Result!); Require(settings.Text.CustomWords.Contains("알파카"), "Draft applied immediately");
                LearnVocabulary("블렌다", "블렌더");
                Require(settings.Text.WordCandidates.ContainsKey("블렌더"), "Concurrent candidate fixture missing");
                window.SaveSettingsForCheck(); Require(!settings.Text.CustomWords.Contains("알파카") && !SettingsStore.Load().Text.CustomWords.Contains("알파카"), "Confirmed backup not restored");
                Require(settings.Text.WordCandidates.Count == 0, "Background update erased the draft reset request");
                Require(settings.Name == "저장 전 이름" && settings.WhisperUrl == url && !settings.Speak, "Vocabulary restore changed unrelated settings");
                Require(File.Exists(AppPaths.ConfigFile + ".backup"), "Manual apply backup missing"); return Task.CompletedTask;
            });
        }
        finally { await server.StopAsync(); timers.Clear(); StopInput(); StopTurn(); overlay.Close(); window.Close(); }
        var path = Path.Combine(AppPaths.WindowsRoot, "artifacts", "vocabulary-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { total = rows.Count, failed, checks = rows }, SettingsStore.Json)); return failed == 0 ? 0 : 1;
    }
    static IEnumerable<T> VocabularyControls<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) foreach (var control in VocabularyControls<T>(child)) yield return control;
    }
}
