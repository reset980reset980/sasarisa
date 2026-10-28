using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.Desktop;

public partial class App
{
    sealed class UntrackedPageFixture : IBrowserPageHost
    {
        internal int Opens; internal CancellationTokenSource? Stop;
        public Task<BrowserPageSnapshot> CaptureAsync(CancellationToken token) => Task.FromResult(new BrowserPageSnapshot([], false));
        public void Open(string url) { Opens++; Stop?.Cancel(); }
        public Func<CancellationToken, Task<string>>? Own(BrowserPageIdentity page, out IDisposable? lifetime) { lifetime = null; throw new Exception("Incomplete snapshot must never claim a tab"); }
    }
    sealed class OwnedVideoFixture : IBrowserVideoDriver
    {
        internal bool Find = true; internal int Closed; string url = "";
        public Task<BrowserWindow?> OpenAsync(string browser, string address, CancellationToken token) { url = address; return Task.FromResult(Find ? new BrowserWindow(new IntPtr(123), 1, 1, "fixture") : null); }
        public Task<BrowserVideoState> ReadAsync(BrowserWindow window, CancellationToken token, bool playback = false) => Task.FromResult(new BrowserVideoState(url, 1, "owned", false, 0, 100));
        public Task<string> ActAsync(BrowserWindow window, string tab, string operation, int seconds, CancellationToken token) { if (operation == "close") Closed++; return Task.FromResult(operation); }
    }
    async Task<int> RunBrowserPageAppChecksAsync(bool edge = false)
    {
        if (!IsolatedPhoneProfile()) return 2;
        InitializeInternetPhoneFixture(); var rows = new List<object>(); int failed = 0;
        var report = Path.Combine(AppPaths.WindowsRoot, "artifacts", edge ? "browser-page-edge-app-checks.json" : "browser-page-app-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var journal = Path.ChangeExtension(report, ".journal.txt"); File.WriteAllText(journal, ""); var journalGate = new object();
        void Mark(string stage) { lock (journalGate) File.AppendAllText(journal, DateTimeOffset.Now.ToString("O") + " " + Thread.CurrentThread.GetApartmentState() + " " + stage + Environment.NewLine); }
        void Save() => File.WriteAllText(report, JsonSerializer.Serialize(new { browser = edge ? "edge" : "chrome", total = rows.Count, failed, checks = rows }, SettingsStore.Json));
        void Require(bool ok, string text) { if (!ok) throw new Exception(text); }
        async Task Check(string name, Func<Task> action) { name = edge ? name.Replace("Chrome", "Edge") : name; Mark("check.begin " + name); try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } finally { Save(); Mark("check.end " + name); } }
        var exe = VideoBrowserLaunch.Binary(edge ? "edge" : "chrome");
        if (exe == null) throw new InvalidOperationException("Chrome is required for the opt-in page fixture");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var server = builder.Build();
        server.MapGet("/{name}", (string name) => Results.Text("<!doctype html><title>Sasarisa page fixture " + name + "</title><input aria-label='fixture input'><p>Disposable local page</p><a href='/changed'>Navigate fixture</a><button onclick='window.onbeforeunload=()=>true'>Mark dirty</button>", "text/html"));
        await server.StartAsync(); var origin = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var profile = Path.Combine(AppPaths.Home, "chrome-fixture-" + Guid.NewGuid().ToString("N"));
        Process Start(string url, bool first = false)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--force-renderer-accessibility" }) info.ArgumentList.Add(arg);
            if (first) info.ArgumentList.Add("--new-window"); info.ArgumentList.Add(url); return Process.Start(info)!;
        }
        using var chrome = Start(origin + "/keep", true);
        Mark("browser.started " + chrome.Id); var host = hands.PageHost(url => { using var forwarded = Start(url); }, handle => { Native.GetWindowThreadProcessId(handle, out var pid); return pid == chrome.Id; }, Mark);
        PageHostForCheck = host;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var token = timeout.Token;
        async Task<BrowserPageSnapshot> Snapshot()
        {
            var value = await host.CaptureAsync(token); Require(value.Complete && value.Windows.Count == 1, "Owned Chrome window not ready"); return value;
        }
        async Task<BrowserPageSnapshot> Until(Func<BrowserPageSnapshot, bool> ready)
        { for (int i = 0; i < 60; i++) { var value = await host.CaptureAsync(token); if (value.Complete && ready(value)) return value; await Task.Delay(100, token); } throw new Exception("Browser fixture condition timed out"); }
        var fixtureAutomation = new BrowserAutomationWork();
        async Task CloseFixtureTab()
        {
            var snapshot = await Snapshot(); var target = snapshot.Windows.Single();
            Require(target.Window.ProcessId == chrome.Id && target.View.Address == origin + "/gone", "Not the disposable external-close fixture");
            await fixtureAutomation.RunAsync(ct =>
            {
                var tabs = AutomationElement.FromHandle(target.Window.Handle).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Cast<AutomationElement>()
                    .Where(e => e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) && ((SelectionItemPattern)pattern).Current.IsSelected).ToArray();
                Require(tabs.Length == 1, "Ambiguous fixture selected tab");
                var buttons = tabs[0].FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                    .Where(e => e.Current.IsEnabled && !e.Current.IsOffscreen && System.Text.RegularExpressions.Regex.IsMatch(e.Current.Name, @"^(?:닫기|탭 닫기|Close|Close tab)(?:\s*\(Ctrl\+W\))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
                Require(buttons.Length == 1, "Ambiguous fixture close button"); var invoke = (InvokePattern)buttons[0].GetCurrentPattern(InvokePattern.Pattern); ct.ThrowIfCancellationRequested(); invoke.Invoke(); return Task.FromResult(true);
            }, token);
        }
        async Task PressPageElement(string label)
        {
            var snapshot = await Snapshot(); var target = snapshot.Windows.Single();
            Require(target.Window.ProcessId == chrome.Id && target.View.Address?.StartsWith(origin + "/", StringComparison.Ordinal) == true, "Not the local fixture page");
            await fixtureAutomation.RunAsync(ct =>
            {
            Mark("fixture.element.begin " + label); var root = AutomationElement.FromHandle(target.Window.Handle);
            var matches = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, label)).Cast<AutomationElement>().Where(e => e.TryGetCurrentPattern(InvokePattern.Pattern, out _)).ToArray();
            Require(matches.Length == 1, "Fixture control is ambiguous"); var invoke = (InvokePattern)matches[0].GetCurrentPattern(InvokePattern.Pattern); ct.ThrowIfCancellationRequested(); Mark("fixture.invoke.begin " + label); invoke.Invoke(); Mark("fixture.invoke.end " + label);
            return Task.FromResult(true);
            }, token);
        }
        try
        {
            await Until(x => x.Windows.Count == 1 && x.Windows[0].View.Address?.Contains("/keep") == true);
            await Check("Actual Chrome App URL undo closes only its new tab and preserves an identical user tab", async () =>
            {
                using (Start(origin + "/same")) { }
                var before = await Until(x => x.Windows.Count == 1 && x.Windows[0].View.Tabs.Count == 2); var ids = before.Windows[0].View.Tabs.Select(t => t.Key).ToArray();
                await ExecuteTrackedAsync(new("open_url", origin + "/same"), token); Require((await Snapshot()).Windows[0].View.Tabs.Count == 3, "New tab missing");
                var result = await ExecuteTrackedAsync(new("undo"), token); var after = await Snapshot(); Require(after.Windows[0].View.Tabs.Select(t => t.Key).SequenceEqual(ids), "Undo result: " + result + "; address=" + after.Windows[0].View.Address + "; tabs=" + string.Join(",", after.Windows[0].View.Tabs.Select(t => t.Key + ":" + t.Selected)) + "; before=" + string.Join(",", ids));
            });
            await Check("Actual Chrome owned-close command closes a tracked page once", async () =>
            {
                var count = (await Snapshot()).Windows[0].View.Tabs.Count;
                await ExecuteTrackedAsync(new("open_url", origin + "/owned"), token); await ExecuteTrackedAsync(new("close_owned"), token);
                Require((await Snapshot()).Windows[0].View.Tabs.Count == count, "Owned-close left its tab");
                await ExecuteTrackedAsync(new("undo"), token); Require((await Snapshot()).Windows[0].View.Tabs.Count == count, "Repeated undo closed another tab");
            });
            await Check("Actual Chrome navigation after opening blocks undo and leaves both tabs", async () =>
            {
                await ExecuteTrackedAsync(new("open_url", origin + "/navigate"), token); var before = await Snapshot(); var target = before.Windows[0].Window;
                await PressPageElement("Navigate fixture");
                await Until(x => x.Windows.Count == 1 && x.Windows[0].View.Address?.Contains("/changed") == true);
                bool rejected = false; try { await ExecuteTrackedAsync(new("undo"), token); } catch (AgentStoppedException) { rejected = true; }
                Require(rejected && (await Snapshot()).Windows[0].View.Tabs.Count == before.Windows[0].View.Tabs.Count, "Navigated tab was closed");
            });
            await Check("Actual Chrome externally closed owned tab never makes undo close another tab", async () =>
            {
                await ExecuteTrackedAsync(new("open_url", origin + "/gone"), token); var before = await Snapshot(); await CloseFixtureTab();
                await Until(x => x.Windows.Count == 1 && x.Windows[0].View.Tabs.Count == before.Windows[0].View.Tabs.Count - 1);
                await ExecuteTrackedAsync(new("undo"), token); Require((await Snapshot()).Windows[0].View.Tabs.Count == before.Windows[0].View.Tabs.Count - 1, "Closed another tab after user close");
            });
            await Check("Actual Chrome cancelled undo sends no close shortcut", async () =>
            {
                await ExecuteTrackedAsync(new("open_url", origin + "/cancel"), token); var count = (await Snapshot()).Windows[0].View.Tabs.Count;
                using var stop = new CancellationTokenSource(); stop.Cancel(); try { await actionHistory.UndoAsync(stop.Token); } catch (OperationCanceledException) { }
                Require((await Snapshot()).Windows[0].View.Tabs.Count == count, "Cancelled undo closed a tab");
                await ExecuteTrackedAsync(new("undo"), token);
            });
            await Check("Actual Chrome closes three new one-tab windows without blocking or closing the original", async () =>
            {
                var before = await Snapshot();
                PageHostForCheck = hands.PageHost(url => { using var child = Start(url, true); }, handle => { Native.GetWindowThreadProcessId(handle, out var pid); return pid == chrome.Id; }, Mark);
                try
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        await ExecuteTrackedAsync(new("open_url", origin + "/new-window-" + attempt), token); await Until(x => x.Windows.Count == 2);
                        await ExecuteTrackedAsync(new("undo"), token); var after = await Until(x => x.Windows.Count == 1);
                        Require(after.Windows[0].Window.Handle == before.Windows[0].Window.Handle && after.Windows[0].View.Tabs.Select(t => t.Key).SequenceEqual(before.Windows[0].View.Tabs.Select(t => t.Key)), "Undo closed the original window");
                    }
                }
                finally { PageHostForCheck = host; }
            });
            await Check("Actual Chrome before-unload confirmation blocks automatic close", async () =>
            {
                await ExecuteTrackedAsync(new("open_url", origin + "/dirty"), token); var before = await Snapshot(); await PressPageElement("Mark dirty");
                bool rejected = false; try { await ExecuteTrackedAsync(new("undo"), token); } catch (InvalidOperationException) { rejected = true; }
                await fixtureAutomation.RunAsync(ct =>
                {
                Mark("fixture.confirmation.begin"); var cancelButtons = new List<AutomationElement>();
                foreach (var handle in OwnedWindow.VisibleWindows())
                {
                    Native.GetWindowThreadProcessId(handle, out var pid); if (pid != chrome.Id) continue;
                    var root = AutomationElement.FromHandle(handle);
                    cancelButtons.AddRange(root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().Where(e => e.Current.IsEnabled && !e.Current.IsOffscreen && System.Text.RegularExpressions.Regex.IsMatch(e.Current.Name, "^(취소|Cancel|머무르기|Stay)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
                }
                var cancel = cancelButtons.GroupBy(e => string.Join(".", e.GetRuntimeId())).Select(g => g.First()).ToArray(); Require(cancel.Length == 1, "Cannot identify own fixture confirmation cancel button");
                var invoke = (InvokePattern)cancel[0].GetCurrentPattern(InvokePattern.Pattern); ct.ThrowIfCancellationRequested(); Mark("fixture.confirmation.invoke.begin"); invoke.Invoke(); Mark("fixture.confirmation.invoke.end"); return Task.FromResult(true);
                }, token);
                var after = await Until(x => x.Windows.Count == 1 && x.Windows[0].View.Tabs.Count == before.Windows[0].View.Tabs.Count);
                Require(rejected && after.Windows[0].View.Tabs.Count == before.Windows[0].View.Tabs.Count, "Before-unload confirmation was bypassed");
            });
            await Check("Untracked URL opening never makes undo or owned-close skip to an older resource", async () =>
            {
                int oldCloses = 0; RememberLaunch("older resource", _ => { oldCloses++; return Task.FromResult("older closed"); });
                var unknown = new UntrackedPageFixture(); PageHostForCheck = unknown;
                await ExecuteTrackedAsync(new("open_url", origin + "/untracked"), token); await ExecuteTrackedAsync(new("undo"), token);
                try { await ExecuteTrackedAsync(new("close_owned"), token); throw new Exception("Unknown launch skipped"); } catch (AgentStoppedException) { }
                Require(oldCloses == 0 && unknown.Opens == 1, "Unknown launch closed an earlier resource or opened repeatedly");
            });
            await Check("Cancellation immediately after URL launch records uncertainty and leaves older resources untouched", async () =>
            {
                using var stop = new CancellationTokenSource(); var unknown = new UntrackedPageFixture { Stop = stop }; PageHostForCheck = unknown;
                bool cancelled = false; try { await ExecuteTrackedAsync(new("open_url", origin + "/cancelled-open"), stop.Token); } catch (OperationCanceledException) { cancelled = true; }
                var reply = await actionHistory.UndoAsync(token); Require(cancelled && unknown.Opens == 1 && reply.Contains("페이지가 열렸을 수"), "Partial launch cancellation was lost");
            });
            await Check("Simulated video uses the shared owned-resource order and cannot close twice", async () =>
            {
                var saved = settings; var fake = new OwnedVideoFixture(); browserVideo = new BrowserVideo(fake);
                try
                {
                    settings = settings with { YouTubePlayer = "chrome" }; await PlayVideoAsync(new("abcdefghijk", "simulated"), token);
                    await ExecuteTrackedAsync(new("close_owned"), token); await actionHistory.UndoAsync(token);
                    Require(fake.Closed == 1 && !browserVideo.Active, "Video resource order or single-use close lost");
                }
                finally { settings = saved; browserVideo.Forget(); browserVideo = null; }
            });
            await Check("An untracked simulated video blocks closing an older owned resource", async () =>
            {
                int closed = 0; RememberLaunch("older", _ => { closed++; return Task.FromResult("closed"); });
                var saved = settings; browserVideo = new BrowserVideo(new OwnedVideoFixture { Find = false });
                try
                {
                    settings = settings with { YouTubePlayer = "chrome" }; await PlayVideoAsync(new("abcdefghijk", "untracked simulation"), token);
                    try { await ExecuteTrackedAsync(new("close_owned"), token); throw new Exception("Skipped unknown video"); } catch (AgentStoppedException) { }
                    Require(closed == 0, "Unknown video closed an older resource");
                }
                finally { settings = saved; browserVideo.Forget(); browserVideo = null; }
            });
        }
        catch (Exception ex) { failed++; rows.Add(new { name = "Owned Chrome fixture lifecycle", pass = false, error = ex.ToString() }); }
        finally
        {
            Mark("cleanup.begin"); foreach (var launch in ownedLaunches) launch.Dispose();
            if (!chrome.HasExited) { chrome.Kill(true); await chrome.WaitForExitAsync(); }
            await server.StopAsync(); StopTurn(); overlay.Close(); window.Close(); Mark("cleanup.end");
        }
        Save(); return failed == 0 ? 0 : 1;
    }
}
