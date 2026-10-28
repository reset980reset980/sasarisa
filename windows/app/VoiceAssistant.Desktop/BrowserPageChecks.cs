using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VoiceAssistant.Desktop;

internal static class BrowserPageChecks
{
    internal static async Task<int> RunAsync(List<object> rows)
    {
        int failed = 0;
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        void Require(bool ok) { if (!ok) throw new Exception("Page ownership invariant failed"); }
        var window = new BrowserWindow(new IntPtr(1), 1, 10, "browser");
        BrowserPageSnapshot View(string address, params BrowserNativeTab[] tabs) => new([new(window, new(tabs, address))]);
        var old = View("https://example.com/", new BrowserNativeTab("user", "same title", true));
        await Check("Page ownership claims only one newly added selected tab and preserves an existing identical URL", () =>
        {
            var after = View("https://example.com/", new("user", "same title", false), new("assistant", "same title", true));
            Require(BrowserPageOwnership.Find(old, after, "https://example.com/")?.Tab == "assistant"); return Task.CompletedTask;
        });
        await Check("A new browser window with one matching tab is owned without adopting other startup tabs", () =>
        {
            var fresh = new BrowserWindow(new IntPtr(2), 2, 20, "new browser");
            var after = new BrowserPageSnapshot([old.Windows[0], new(fresh, new([new("new", "", true)], "https://example.org/"))]);
            Require(BrowserPageOwnership.Find(old, after, "https://example.org/")?.Window == fresh); return Task.CompletedTask;
        });
        await Check("Existing reused tabs, redirects, background tabs and ambiguous new tabs remain unclaimed", () =>
        {
            Require(BrowserPageOwnership.Find(old, old, "https://example.com/") == null);
            Require(BrowserPageOwnership.Find(old, View("https://other.example/", new("user", "", false), new("assistant", "", true)), "https://example.com/") == null);
            Require(BrowserPageOwnership.Find(old, View("https://example.com/", new("user", "", true), new("assistant", "", false)), "https://example.com/") == null);
            Require(BrowserPageOwnership.Find(old, View("https://example.com/", new("user", "", false), new("one", "", true), new("two", "", false)), "https://example.com/") == null); return Task.CompletedTask;
        });
        await Check("Missing old tabs, process/window replacement and incomplete snapshots prevent attribution", () =>
        {
            var after = View("https://example.com/", new BrowserNativeTab("assistant", "", true));
            Require(BrowserPageOwnership.Find(old, after, "https://example.com/") == null);
            var replaced = new BrowserPageSnapshot([new(window with { Started = 11 }, new([new("user", "", false), new("assistant", "", true)], "https://example.com/"))]);
            Require(BrowserPageOwnership.Find(old, replaced, "https://example.com/") == null);
            Require(BrowserPageOwnership.Find(old with { Complete = false }, old, "https://example.com/") == null); return Task.CompletedTask;
        });
        await Check("URL comparison keeps scheme, path, query and fragment boundaries", () =>
        {
            Require(BrowserPageOwnership.SameUrl("https://EXAMPLE.COM/a?q=1#two", "https://example.com/a?q=1#two"));
            foreach (var url in new[] { "http://example.com/a?q=1#two", "https://example.com/A?q=1#two", "https://example.com/a?q=2#two", "https://example.com/a?q=1#other", "example.com/a?q=1#two" }) Require(!BrowserPageOwnership.SameUrl(url, "https://example.com/a?q=1#two"));
            Require(BrowserPageOwnership.DisplayMatches("127.0.0.1:1234/page", "http://127.0.0.1:1234/page"));
            Require(!BrowserPageOwnership.DisplayMatches("example.com/a", "https://example.com/a/"));
            Require(!BrowserPageOwnership.DisplayMatches("https://example.com/", "http://example.com/"));
            return Task.CompletedTask;
        });
        await Check("Owned launch closure is single-use, cancellation-safe and retryable after a blocked close", async () =>
        {
            int calls = 0; bool blocked = true;
            using var launch = new OwnedLaunch("page", ct => { ct.ThrowIfCancellationRequested(); calls++; if (blocked) throw new InvalidOperationException("before unload"); return Task.FromResult("closed"); });
            using var stop = new CancellationTokenSource(); stop.Cancel(); try { await launch.CloseAsync(stop.Token); } catch (OperationCanceledException) { }
            Require(calls == 0 && !launch.Closed);
            try { await launch.CloseAsync(default); } catch (InvalidOperationException) { }
            Require(calls == 1 && !launch.Closed); blocked = false; await launch.CloseAsync(default); await launch.CloseAsync(default); Require(calls == 2 && launch.Closed);
        });
        await Check("An edited omnibox cannot impersonate the loaded page URL", () =>
        {
            Require(BrowserPageOwnership.VerifiedAddress("example.com/owned", ["https://example.com/owned"]) == "https://example.com/owned");
            Require(BrowserPageOwnership.VerifiedAddress("https://example.com/owned", ["https://example.com/user-work"]) == null);
            Require(BrowserPageOwnership.VerifiedAddress("example.com/owned", ["https://example.com/owned", "https://other.example/"]) == null); return Task.CompletedTask;
        });
        await Check("Browser automation uses an MTA worker while the WPF dispatcher remains responsive", async () =>
        {
            var worker = new BrowserAutomationWork(); var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var caller = Environment.CurrentManagedThreadId; var dispatcher = Dispatcher.CurrentDispatcher;
            var pending = worker.RunAsync(async ct => { Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA); entered.SetResult(Environment.CurrentManagedThreadId); await release.Task; ct.ThrowIfCancellationRequested(); return true; }, default);
            try
            {
                Require(await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)) != caller);
                _ = dispatcher.BeginInvoke(() => heartbeat.TrySetResult()); await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { release.TrySetResult(); }
            Require(await pending);
        });
        await Check("A blocked UIA lookup times out without queuing or later performing a cancelled mutation", async () =>
        {
            var worker = new BrowserAutomationWork(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int mutations = 0;
            var pending = worker.RunAsync(ct => { entered.Set(); release.Wait(); ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref mutations); return Task.FromResult(true); }, default, TimeSpan.FromMilliseconds(80));
            try
            {
                Require(entered.Wait(2000)); bool stopped = false; try { await pending; } catch (AgentStoppedException) { stopped = true; }
                Require(stopped && worker.IsBusy && mutations == 0);
                bool blocked = false; try { await worker.RunAsync(_ => { mutations++; return Task.FromResult(true); }, default); } catch (AgentStoppedException) { blocked = true; }
                Require(blocked && mutations == 0);
            }
            finally { release.Set(); for (int i = 0; i < 100 && worker.IsBusy; i++) await Task.Delay(10); }
            Require(!worker.IsBusy && mutations == 0);
            Require(await worker.RunAsync(_ => Task.FromResult(true), default));
        });
        await Check("Browser automation cancellation and provider exceptions release safely for retry", async () =>
        {
            var worker = new BrowserAutomationWork(); int calls = 0; using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await worker.RunAsync(_ => { calls++; return Task.FromResult(true); }, stop.Token); } catch (OperationCanceledException) { }
            Require(calls == 0 && !worker.IsBusy);
            using var activeStop = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = worker.RunAsync(async ct => { entered.SetResult(); await release.Task; ct.ThrowIfCancellationRequested(); calls++; return true; }, activeStop.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); activeStop.Cancel(); bool cancelled = false;
                try { await pending; } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && worker.IsBusy && calls == 0);
            }
            finally { release.TrySetResult(); for (int i = 0; i < 100 && worker.IsBusy; i++) await Task.Delay(10); }
            Require(calls == 0 && !worker.IsBusy);
            try { await worker.RunAsync<bool>(_ => throw new InvalidOperationException("provider failed"), default); } catch (InvalidOperationException) { }
            for (int i = 0; i < 100 && worker.IsBusy; i++) await Task.Delay(10);
            Require(!worker.IsBusy && await worker.RunAsync(_ => Task.FromResult(true), default));
        });
        return failed;
    }
}
