using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class BrowserVideoChecks
{
    sealed class Driver : IBrowserVideoDriver
    {
        public BrowserVideoState State = new("https://www.youtube.com/watch?v=abcdefghijk", 1, "owned-tab", true, 10, 100);
        public readonly List<string> Calls = new();
        public bool Find = true, FailPause;
        public Task<BrowserWindow?> OpenAsync(string browser, string url, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add("open:" + browser); State = State with { Url = url, Tabs = 1, Tab = "owned-tab" }; return Task.FromResult(Find ? new BrowserWindow(new IntPtr(123), 1, 1, "owned") : null); }
        public Task<BrowserVideoState> ReadAsync(BrowserWindow window, CancellationToken token, bool playback = false) { token.ThrowIfCancellationRequested(); return Task.FromResult(State); }
        public Task<string> ActAsync(BrowserWindow window, string tab, string operation, int seconds, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailPause && operation == "pause") throw new InvalidOperationException("pause failed"); Calls.Add(operation); return Task.FromResult(operation); }
    }
    public static async Task<int> RunAsync(List<object> rows, bool native = false)
    {
        int failed = 0;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var video = new YouTubeVideo("abcdefghijk", "fixture");
        await Check("YouTube browser accepts only HTTPS watch pages and preserves the saved player choice", () =>
        {
            Require(BrowserVideo.IsWatchUrl(video.WatchUrl) && BrowserVideo.IsWatchUrl("www.youtube.com/watch?v=abcdefghijk"), "Valid watch URL rejected");
            Require(BrowserVideo.VideoId(video.WatchUrl) == BrowserVideo.VideoId("youtube.com/watch?v=abcdefghijk"), "Omnibox www or autoplay normalization lost video identity");
            foreach (var value in new[] { "https://accounts.google.com/", "https://www.youtube.com/", "https://youtube.com.evil.test/watch?v=abcdefghijk", "https://user@youtube.com/watch?v=abcdefghijk", "http://youtube.com/watch?v=abcdefghijk", "https://youtube.com:444/watch?v=abcdefghijk", "https://youtube.com/watch?v=short" }) Require(!BrowserVideo.IsWatchUrl(value), value);
            var copy = SettingsTransfer.Parse(SettingsTransfer.Serialize(new UiSettings { YouTubePlayer = "edge" })); Require(copy.YouTubePlayer == "edge", "Playback preference lost");
            return Task.CompletedTask;
        });
        await Check("YouTube browser replacement pauses and closes only the tracked video before opening another", async () =>
        {
            var driver = new Driver(); var session = new BrowserVideo(driver); await session.OpenAsync("chrome", video, default); await session.OpenAsync("edge", video, default);
            Require(driver.Calls.SequenceEqual(new[] { "open:chrome", "pause", "close", "open:edge" }), "Old audio can overlap");
            await session.ControlAsync("close", 0, default); Require(!session.Active, "Closed session retained");
        });
        await Check("YouTube browser refuses controls after login navigation or tab changes", async () =>
        {
            foreach (var changed in new[] { new BrowserVideoState("https://accounts.google.com/", 1, "owned-tab", null, null, null), new BrowserVideoState(video.WatchUrl, 2, "owned-tab", true, 0, 100), new BrowserVideoState(video.WatchUrl, 1, "different", true, 0, 100) })
            {
                var driver = new Driver(); var session = new BrowserVideo(driver); await session.OpenAsync("chrome", video, default); driver.State = changed;
                try { await session.ControlAsync("close", 0, default); throw new Exception("Changed browser was controlled"); } catch (InvalidOperationException) { }
                Require(driver.Calls.Count == 1, "Unrelated browser action occurred");
            }
        });
        await Check("YouTube browser leaves unrecognized windows and user-added tabs alone", async () =>
        {
            var driver = new Driver { Find = false }; var session = new BrowserVideo(driver); var answer = await session.OpenAsync("chrome", video, default);
            Require(!session.Active && answer.Contains("확인하지 못"), "Unrecognized browser claimed");
            driver.Find = true; await session.OpenAsync("chrome", video, default); driver.State = driver.State with { Tabs = 2 }; await session.OpenAsync("chrome", video, default);
            Require(driver.Calls.All(x => x.StartsWith("open:")), "User-added tab was closed");
        });
        await Check("YouTube browser cancellation and failed pause do not open overlapping videos", async () =>
        {
            var driver = new Driver(); var session = new BrowserVideo(driver); await session.OpenAsync("chrome", video, default); driver.FailPause = true;
            try { await session.OpenAsync("chrome", video, default); throw new Exception("Failed pause accepted"); } catch (InvalidOperationException) { }
            Require(session.Active && driver.Calls.Count == 1, "Failed pause lost ownership or opened another video");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await session.ControlAsync("playpause", 0, stop.Token); throw new Exception("Cancelled action ran"); } catch (OperationCanceledException) { }
            Require(driver.Calls.Count == 1, "Cancelled control reached driver");
        });
        if (native)
            await Check("YouTube actual Chrome window opens with the normal profile, pauses, rejects unsupported seek and closes", async () =>
            {
                var driver = new WindowsActions().VideoDriver(); var session = new BrowserVideo(driver);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var real = new YouTubeVideo("aqz-KE-bpKQ", "Big Buck Bunny");
                try
                {
                    var result = await session.OpenAsync("chrome", real, timeout.Token); Require(session.Active, result);
                    AppPaths.Log("video-native-check", "opened");
                    await Task.Delay(2500, timeout.Token);
                    await session.ControlAsync("pause", 0, timeout.Token);
                    var paused = await session.PlaybackAsync(timeout.Token);
                    for (int i = 0; i < 8 && (paused.Playing != false || paused.Position == null); i++) { await Task.Delay(200, timeout.Token); paused = await session.PlaybackAsync(timeout.Token); }
                    Require(paused.Playing == false && paused.Position != null, "Pause was not observed: " + System.Text.Json.JsonSerializer.Serialize(paused));
                    AppPaths.Log("video-native-check", "paused position=" + paused.Position + " duration=" + paused.Duration);
                    try { await session.ControlAsync("seek", 5, timeout.Token); throw new Exception("Unverified seek reported success"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("아직 지원하지")) { }
                    await session.ControlAsync("close", 0, timeout.Token); Require(!session.Active, "Chrome window not released");
                }
                finally { if (session.Active) try { await session.ControlAsync("close", 0, CancellationToken.None); } catch (Exception) { } }
            });
        return failed;
    }
}
