using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace VoiceAssistant.Desktop;

internal static class BargeInChecks
{
    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    internal static async Task<int> RunAsync(List<object> rows, bool native)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test)
        { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Barge-in distinguishes headphone speech, speaker wake words, and disabled mode", () =>
        {
            var b = new BargeInSession();
            b.Begin("안녕하세요. 준비됐어요.", "자비스", "auto", "G435 Wireless Gaming Headset");
            Require(b.Any && b.AcceptText("새 창을 열어", false), "Headphone user speech ignored");
            Require(!b.AcceptText("준비됐어요", true) && !b.AcceptText("아", true), "Own voice or tiny fragment interrupted");
            b.End(true); b.Begin("안녕하세요. 준비됐어요.", "자비스", "auto", "Speakers (Realtek)");
            Require(!b.Any && !b.AcceptText("새 창을 열어", false), "Speaker text without wake interrupted");
            Require(b.AcceptText("자 비 스 메모장 열어", false) && b.AcceptText("그만", false) && b.AcceptText("재비스 메모장", true), "Wake/stop/worker wake metadata ignored");
            b.End(true); b.Begin("안녕하세요", "자비스", "off", "Headset"); Require(!b.AcceptText("그만", true), "Disabled mode interrupted");
            Require(!BargeInSession.Headphones("외장 Headphones"), "External output exception lost");
            return Task.CompletedTask;
        });
        await Check("Barge-in rejects near echoes and strips only the configured leading form of address", () =>
        {
            var b = new BargeInSession(); b.Begin("오늘은 좋은 날씨예요", "자비스", "any", "Speaker");
            Require(b.IsEcho("오늘은 좋은 날씨에요"), "Near echo accepted");
            Require(BargeInSession.DropGreeting("형님. 형님, 메모장 열어", "형님") == "메모장 열어", "Echo address retained");
            Require(BargeInSession.DropGreeting("형님한테 전화해", "형님") == "형님한테 전화해", "User word was truncated");
            Require(BargeInSession.DropGreeting("형님", "형님") == "형님", "Whole user phrase erased");
            return Task.CompletedTask;
        });
        await Check("Measured echo alone cannot interrupt; sustained voiced user signal can", () =>
        {
            var now = DateTimeOffset.UtcNow; var echo = new DoubleTalk(() => now); echo.Begin();
            for (var i = 0; i < 20; i++) { now = now.AddMilliseconds(100); echo.Played(.5); Require(!echo.Listen(.06, .9), "Own echo interrupted"); }
            echo.Played(.5); Require(!echo.Listen(.25, .9), "First user frame interrupted");
            echo.Played(.5); Require(!echo.Listen(.25, .9), "Second user frame interrupted");
            echo.Played(.5); Require(echo.Listen(.25, .9), "Three user frames ignored");
            echo.End(true); Require(Math.Abs(echo.Coupling - .12) < .00001, "Interrupted output changed calibration");
            return Task.CompletedTask;
        });
        await Check("Barge-in rejects unvoiced bumps and requires speech above recent ambient noise", () =>
        {
            var now = DateTimeOffset.UtcNow; var echo = new DoubleTalk(() => now);
            for (var i = 0; i < 10; i++) echo.Listen(.1, 0);
            echo.Begin();
            for (var i = 0; i < 6; i++) { now = now.AddMilliseconds(100); echo.Played(.1); Require(!echo.Listen(.2, .9), "Ambient sound interrupted"); }
            for (var i = 0; i < 6; i++) { now = now.AddMilliseconds(100); Require(!echo.Listen(.6, .1), "Unvoiced noise interrupted"); }
            Require(!echo.Listen(.6, .9), "One transient interrupted");
            echo.Listen(0, 0); echo.Listen(0, 0); Require(!echo.Listen(0, 0), "Old transient persisted");
            return Task.CompletedTask;
        });
        await Check("Echo learning excludes muted input and persists valid calibration per device pair", () =>
        {
            var now = DateTimeOffset.UtcNow; var echo = new DoubleTalk(() => now); echo.Begin();
            for (var i = 0; i < 25; i++) { now = now.AddMilliseconds(100); echo.Played(.5); echo.Listen(.00001, 0); }
            echo.End(false); Require(echo.Coupling == .12, "Muted mic changed calibration");
            echo.Begin(); for (var i = 0; i < 25; i++) { now = now.AddMilliseconds(100); echo.Played(.5); echo.Listen(.1, .9); }
            echo.End(false); Require(Math.Abs(echo.Coupling - .144) < .00001, "Quiet-room calibration not learned");
            var path = Path.Combine(AppPaths.Home, "work", "echo-check-" + Guid.NewGuid().ToString("N") + ".json");
            var key = EchoCalibration.Key("mic1", "speaker1"); new EchoCalibration(path).Save(key, echo.Coupling);
            Require(new EchoCalibration(path).Get(key) == echo.Coupling && new EchoCalibration(path).Get(EchoCalibration.Key("mic2", "speaker1")) == .12, "Calibration crossed devices or did not persist");
            return Task.CompletedTask;
        });
        await Check("Explicit wake mode disables acoustic interruption and old settings import defaults to auto", () =>
        {
            var b = new BargeInSession(); b.Begin("답변", "자비스", "wake", "Speaker");
            for (var i = 0; i < 5; i++) Require(!b.Listen(.8, .9), "Wake-only mode accepted acoustic trigger");
            var original = JsonSerializer.Deserialize<UiSettings>("{}")!; Require(original.BargeIn == "auto", "Old profile lost auto mode");
            var imported = SettingsTransfer.Parse(SettingsTransfer.Serialize(original with { BargeIn = "off" }));
            var restored = SettingsTransfer.Prepare(imported, original, false, false);
            Require(restored.BargeIn == "off" && SettingsTransfer.Describe(original, restored).Contains("끼어들기"), "Import omitted preference");
            try { SettingsStore.Validate(original with { BargeIn = "invalid" }); } catch (ArgumentException) { return Task.CompletedTask; }
            throw new Exception("Unknown mode accepted");
        });
        await Check("Playback measurement uses actual scaled samples through both provider read methods", () =>
        {
            var folder = Path.Combine(AppPaths.Home, "work"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "measured-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
                    for (var i = 0; i < 3200; i++) writer.WriteSample(.25f);
                using var reader = new AudioFileReader(path); reader.Volume = .4f;
                var levels = new List<double>(); var provider = new MeasuredVoiceProvider(reader, levels.Add);
                var bytes = new byte[6400];
                Require(provider.Read(bytes.AsSpan()) > 0 && provider.Read(bytes, 0, bytes.Length) > 0, "Audio was not read");
                Require(levels.Count == 2 && levels.All(x => Math.Abs(x - .1) < .0001), "Playback volume not included in RMS");
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        });
        if (native)
        {
            VoiceAudio? rendered = null;
            await Check("Native Korean SAPI rendering produces bounded PCM and cleans the temporary file", async () =>
            {
                rendered = await SapiSpeech.RenderAsync("안녕하세요. 음성 출력 시험입니다.", new UiSettings(), CancellationToken.None);
                using var bytes = new MemoryStream(rendered.Bytes); using var reader = new WaveFileReader(bytes);
                Require(reader.TotalTime.TotalSeconds is > 1 and < 20 && reader.WaveFormat.Channels == 1, "Invalid native speech WAV");
                Require(!Directory.EnumerateFiles(Path.Combine(AppPaths.Home, "voice-temp"), "sapi-*.wav").Any(), "Rendered file not cleaned before completion");
            });
            await Check("Native measured playback delivers start-level-end events and respects zero volume", async () =>
            {
                Require(rendered != null, "Native rendering unavailable"); var events = new List<string>(); var sync = new object(); var maximum = 0.0;
                var observer = new VoicePlaybackObserver(device => { lock (sync) events.Add("start"); Require(device.Length > 0, "Output identity missing"); },
                    level => { lock (sync) { maximum = Math.Max(maximum, level); events.Add("level"); } },
                    interrupted => { lock (sync) events.Add(interrupted ? "cancel" : "end"); });
                await VoicePlayback.PlayAsync(rendered!, new UiSettings { SpeechVolume = 0 }, CancellationToken.None, observer);
                Require(events.First() == "start" && events.Last() == "end" && events.Contains("level") && maximum == 0, "Invalid event order or volume not applied before measurement");
            });
            await Check("Native playback cancellation interrupts output once without resuming or leaking audio", async () =>
            {
                Require(rendered != null, "Native rendering unavailable"); using var stop = new CancellationTokenSource(); var ended = 0; var interrupted = false; var cancelled = false;
                var observer = new VoicePlaybackObserver(_ => stop.CancelAfter(250), _ => { }, value => { ended++; interrupted = value; });
                try { await VoicePlayback.PlayAsync(rendered!, new UiSettings { SpeechVolume = 0 }, stop.Token, observer); } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && interrupted && ended == 1, "Output did not cancel exactly once");
                Require(!Directory.EnumerateFiles(Path.Combine(AppPaths.Home, "voice-temp")).Any(), "Temporary audio left behind");
            });
        }
        return failed;
    }
}
