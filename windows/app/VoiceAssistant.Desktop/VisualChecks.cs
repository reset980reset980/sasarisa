using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;

namespace VoiceAssistant.Desktop;

internal static class VisualChecks
{
    sealed class Driver(VisualFrame frame) : IVisualDriver
    {
        internal VisualFrame Frame = frame;
        internal int Clicks;
        public Task<VisualFrame> CaptureAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Frame); }
        public Task ClickAsync(VisualFrame expected, int x, int y, CancellationToken token) { token.ThrowIfCancellationRequested(); if (!ReferenceEquals(expected, Frame)) throw new Exception("Stale frame reached native click"); Clicks++; return Task.CompletedTask; }
    }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OperationCanceledException) { return; } throw new Exception("Unsafe visual operation accepted"); }
    internal static VisualFrame Fixture(bool blueOnRight = true)
    {
        using var bitmap = new Bitmap(640, 360);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White); g.FillRectangle(blueOnRight ? Brushes.Red : Brushes.Blue, 70, 90, 180, 170); g.FillRectangle(blueOnRight ? Brushes.Blue : Brushes.Red, 390, 90, 180, 170);
            using var font = new Font("Arial", 26); g.DrawString("CHOOSE BLUE", font, Brushes.Black, 160, 20);
        }
        return VisualNative.Encode(bitmap, new(-1280, 100, 1280, 720), "fixture", "Visual fixture");
    }
    static VisualFrame Change(VisualFrame source, int x, int y, int width, int height)
    {
        var pixels = (byte[])source.Pixels.Clone();
        for (var yy = y; yy < y + height; yy++) for (var xx = x; xx < x + width; xx++) { var at = (yy * source.Image.Width + xx) * 4; pixels[at] = pixels[at + 1] = pixels[at + 2] = 0; }
        return source with { Pixels = pixels };
    }
    static AssistantImage LargeTransportImage()
    {
        using var bitmap = new Bitmap(320, 240, PixelFormat.Format32bppArgb); var bytes = new byte[320 * 240 * 4]; new Random(71).NextBytes(bytes);
        for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        var data = bitmap.LockBits(new Rectangle(0, 0, 320, 240), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); } finally { bitmap.UnlockBits(data); }
        return VisualNative.Encode(bitmap, new(0, 0, 320, 240), "fixture", "transport fixture").Image;
    }
    internal static async Task<int> RunAsync(List<object> rows, bool claude, bool codex)
    {
        var failed = 0;
        async Task Check(string name, Func<Task> test) { try { await test(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Visual tool coordinates are strict and always confirmed; image references never become routines", async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            var action = McpTools.Decode("click", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { screenshot_id = id, x = 480, y = 175 })));
            Require(action == new AssistantAction("click", id, X: 480, Y: 175), "Image coordinate mapping failed");
            Require(ActionPolicy.NeedsConfirmation(action, true) && ActionPolicy.NeedsConfirmation(new("screenshot"), true), "Missing visual confirmation");
            Require(!Personalization.CanReuse([action]), "Coordinates were reusable");
            foreach (var json in new[] { "{}", "{\"screenshot_id\":\"fake\",\"x\":1,\"y\":1}", JsonSerializer.Serialize(new { screenshot_id = id, x = -1, y = 1 }), JsonSerializer.Serialize(new { screenshot_id = id, x = 1280, y = 1 }), JsonSerializer.Serialize(new { screenshot_id = id, x = 1.5, y = 1 }), JsonSerializer.Serialize(new { screenshot_id = id, x = "1", y = 1 }), JsonSerializer.Serialize(new { screenshot_id = id, x = 1, y = 1, extra = "command" }) })
                await Reject(() => { McpTools.Decode("click", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)); return Task.CompletedTask; });
        });
        await Check("Visual coordinates map image pixel centers across scale and negative desktop origins", () =>
        {
            var frame = Fixture();
            Require(VisualControls.ToScreen(frame, 0, 0) == (-1279, 101), "Origin scale wrong");
            Require(VisualControls.ToScreen(frame, 639, 359) == (-1, 819), "Last pixel exceeded frame");
            return Task.CompletedTask;
        });
        await Check("Only latest unexpired image can be clicked once; refusal paths produce no native click", async () =>
        {
            var now = DateTimeOffset.UtcNow; var driver = new Driver(Fixture()); var visual = new VisualControls(driver, () => now);
            var old = await visual.CaptureAsync(default); var current = await visual.CaptureAsync(default);
            await Reject(() => visual.ClickAsync(old.Id, 480, 175, default));
            await Reject(() => visual.ClickAsync(current.Id, 640, 175, default));
            await visual.ClickAsync(current.Id, 480, 175, default); Require(driver.Clicks == 1, "Correct reference did not click");
            await Reject(() => visual.ClickAsync(current.Id, 480, 175, default));
            current = await visual.CaptureAsync(default); now += TimeSpan.FromSeconds(121); await Reject(() => visual.ClickAsync(current.Id, 480, 175, default));
            current = await visual.CaptureAsync(default); visual.Invalidate(); await Reject(() => visual.ClickAsync(current.Id, 480, 175, default));
            Require(driver.Clicks == 1, "Invalid reference clicked");
        });
        await Check("Changed click neighborhood, moved window and cancelled request block input", async () =>
        {
            var original = Fixture(); var driver = new Driver(original); var visual = new VisualControls(driver); var image = await visual.CaptureAsync(default);
            driver.Frame = Change(original, 450, 140, 50, 60); await Reject(() => visual.ClickAsync(image.Id, 480, 175, default));
            driver.Frame = original; image = await visual.CaptureAsync(default); driver.Frame = original with { Bounds = original.Bounds with { Left = -1200 } };
            await Reject(() => visual.ClickAsync(image.Id, 480, 175, default));
            driver.Frame = original; image = await visual.CaptureAsync(default); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Reject(() => visual.ClickAsync(image.Id, 480, 175, cancelled.Token)); Require(driver.Clicks == 0, "Changed or cancelled image clicked");
            Require(VisualControls.SamePoint(original, Change(original, 10, 330, 10, 10), 480, 175), "Small unrelated clock/caret update invalidates image");
            Require(!VisualControls.SamePoint(original, Change(original, 0, 270, 640, 80), 480, 175), "Major scene change was ignored");
        });
        await Check("Large raster image stays bounded and preserves aspect ratio with JPEG fallback", () =>
        {
            using var bitmap = new Bitmap(1500, 1500); var random = new Random(417);
            using (var g = Graphics.FromImage(bitmap)) for (var y = 0; y < 1500; y += 3) for (var x = 0; x < 1500; x += 3) { using var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256))); g.FillRectangle(brush, x, y, 3, 3); }
            var frame = VisualNative.Encode(bitmap, new(0, 0, 1500, 1500), "fixture", "noise");
            Require(frame.Image.Width == 1280 && frame.Image.Height == 1280 && frame.Image.MimeType == "image/jpeg" && Convert.FromBase64String(frame.Image.Base64).Length <= 1_300_000, "Image limits or fallback failed");
            return Task.CompletedTask;
        });
        foreach (var version in new[] { "2025-06-18", "2026-07-28" })
            await Check("MCP real stdio transmits an actual image content block " + version, async () =>
            {
                var image = LargeTransportImage(); Require(image.Base64.Length > 64 * 1024, "Transport image did not exceed text-only frame limit"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var session = new McpSession((_, _) => Task.FromResult(new McpReply(false, "succeeded", "image fixture", "{\"fixture\":true}", image)), timeout.Token);
                await using var client = await McpChecks.Connect(session.Endpoint, version, timeout.Token);
                var response = await client.CallToolAsync("screenshot", cancellationToken: timeout.Token);
                Require(response.IsError != true && response.Content.OfType<TextContentBlock>().Single().Text.Length < 1000, "Image was embedded in model text");
                var actual = response.Content.OfType<ImageContentBlock>().Single();
                Require(actual.MimeType == image.MimeType && actual.DecodedData.Span.SequenceEqual(Convert.FromBase64String(image.Base64)), "Image bytes damaged in actual MCP transport");
            });
        foreach (var provider in new[] { ("claude", claude), ("codex", codex) }.Where(x => x.Item2))
            await Check("Actual " + provider.Item1 + " sees MCP image and selects blue rectangle coordinates", async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125));
                var right = provider.Item1 == "claude"; var frame = Fixture(right); var imageId = Guid.NewGuid().ToString("N"); var sawImage = false; var clicked = false;
                var run = new McpAgentRun((_, _) => Task.FromResult(true), (action, token) =>
                {
                    if (action.Kind == "front") return Task.FromResult(new ActionOutcome("현재 그림 시험 창", "{\"Application\":\"visual-fixture\",\"Window\":\"그림 시험\",\"CanContinue\":true}"));
                    if (action.Kind == "list_controls") return Task.FromResult(new ActionOutcome("접근성으로 조작할 항목 없음", "{\"Controls\":[],\"Total\":0}"));
                    if (action.Kind == "screenshot") { sawImage = true; return Task.FromResult(new ActionOutcome("실제 이미지", JsonSerializer.Serialize(new { screenshot_id = imageId, width = 640, height = 360 }), frame.Image)); }
                    if (action.Kind == "click" && sawImage && action.Value == imageId && action.X > (right ? 390 : 70) && action.X < (right ? 570 : 250) && action.Y > 90 && action.Y < 260 && !clicked)
                    { clicked = true; return Task.FromResult(new ActionOutcome("파란 사각형 눌림")); }
                    throw new AgentStoppedException("Unexpected visual test action: " + action.Kind + $" ({action.X},{action.Y})");
                }, null, null);
                await using var session = new McpSession(run.DispatchAsync, timeout.Token);
                var answer = await new Brain().AskAsync("현재 창의 그림에서 파란 사각형 한가운데를 한 번 눌러 줘. 접근성에 없으면 screenshot으로 직접 이미지를 본 뒤 click의 x/y를 결정해. 다른 곳은 누르지 마.", "", new UiSettings { BrainProvider = provider.Item1 }, timeout.Token, mcp: session.Endpoint);
                var result = run.Finish(answer); Require(sawImage && clicked && result.Finished, "Visual tool chain failed: " + result.Reply);
            });
        return failed;
    }
}
