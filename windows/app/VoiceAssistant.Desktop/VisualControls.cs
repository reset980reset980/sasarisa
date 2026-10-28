using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record AssistantImage(string MimeType, string Base64, int Width, int Height);
internal sealed record WindowPixels(int Left, int Top, int Width, int Height);
internal sealed record VisualFrame(string Application, string Title, WindowPixels Bounds, AssistantImage Image, byte[] Pixels);
internal sealed record VisualObservation(string Id, VisualFrame Frame);
internal sealed record VisualPreview(AssistantImage Image, int X, int Y);
internal sealed record VisualTarget(string Identity, string Name, string Context, string Role, string State, string Revision);
internal sealed record VisualClickReview(VisualPreview Preview, ActionEffect Effect);
internal interface IVisualDriver
{
    Task<VisualFrame> CaptureAsync(CancellationToken token);
    Task ClickAsync(VisualFrame expected, int x, int y, CancellationToken token);
    Task<VisualTarget?> TargetAsync(VisualFrame expected, int x, int y, CancellationToken token) => Task.FromResult<VisualTarget?>(null);
}

internal sealed class VisualControls(IVisualDriver driver, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    VisualObservation? current;
    DateTimeOffset captured;
    (string Id, int X, int Y, VisualTarget? Target)? prepared;
    internal void Invalidate() { current = null; prepared = null; }
    internal async Task<VisualObservation> CaptureAsync(CancellationToken token)
    {
        Invalidate(); var frame = await driver.CaptureAsync(token); token.ThrowIfCancellationRequested(); Validate(frame);
        captured = now(); return current = new(Guid.NewGuid().ToString("N"), frame);
    }
    static void Validate(VisualFrame frame)
    {
        var image = frame.Image;
        if (image.Width is < 1 or > 1280 || image.Height is < 1 or > 1280 || frame.Bounds.Width < 1 || frame.Bounds.Height < 1 || frame.Pixels.Length != image.Width * image.Height * 4 || image.MimeType is not ("image/png" or "image/jpeg") || image.Base64.Length > 1_740_000)
            throw new InvalidOperationException("화면 이미지의 크기나 형식이 올바르지 않아요.");
    }
    VisualObservation Resolve(string id, int x, int y)
    {
        if (current == null || current.Id != id || now() - captured > TimeSpan.FromSeconds(120)) throw new InvalidOperationException("현재 요청에서 새 화면 이미지를 먼저 확인하세요.");
        if (x < 0 || y < 0 || x >= current.Frame.Image.Width || y >= current.Frame.Image.Height) throw new ArgumentException("누를 위치가 화면 이미지 밖에 있어요.");
        return current;
    }
    internal VisualPreview Preview(string id, int x, int y) => new(Resolve(id, x, y).Frame.Image, x, y);
    internal async Task<VisualClickReview> PrepareClickAsync(string id, int x, int y, CancellationToken token)
    {
        var image = Resolve(id, x, y); prepared = null; token.ThrowIfCancellationRequested();
        var target = await driver.TargetAsync(image.Frame, x, y, token); token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(image, Resolve(id, x, y))) throw new AgentStoppedException("대상을 확인하는 동안 이미지가 바뀌었어요.");
        prepared = (id, x, y, target);
        var effect = target == null ? ActionEffect.Unknown : ExecutionConsent.LabelEffect(target.Name, target.Context + " / " + image.Frame.Title);
        return new(new(image.Frame.Image, x, y), effect);
    }
    internal async Task ClickAsync(string id, int x, int y, CancellationToken token)
    {
        var reference = Resolve(id, x, y);
        try
        {
            token.ThrowIfCancellationRequested(); var fresh = await driver.CaptureAsync(token); Validate(fresh);
            if (fresh.Application != reference.Frame.Application || fresh.Title != reference.Frame.Title || fresh.Bounds != reference.Frame.Bounds || fresh.Image.Width != reference.Frame.Image.Width || fresh.Image.Height != reference.Frame.Image.Height)
                throw new AgentStoppedException("이미지를 확인한 뒤 창의 위치·크기·제목이 바뀌어 누르지 않았어요.");
            if (!SamePoint(reference.Frame, fresh, x, y)) throw new InvalidOperationException("누를 위치 주변의 화면이 바뀌었어요. 새 화면 이미지를 확인한 뒤 다시 요청하세요.");
            if (prepared is { } permission)
            {
                if (permission.Id != id || permission.X != x || permission.Y != y) throw new AgentStoppedException("확인한 위치와 다른 클릭 요청이라 멈췄어요.");
                var target = await driver.TargetAsync(fresh, x, y, token); token.ThrowIfCancellationRequested();
                if (target != permission.Target) throw new AgentStoppedException("확인한 버튼의 이름·문맥·상태가 바뀌어 누르지 않았어요.");
            }
            token.ThrowIfCancellationRequested(); await driver.ClickAsync(fresh, x, y, token);
        }
        finally { Invalidate(); }
    }
    internal static bool SamePoint(VisualFrame before, VisualFrame after, int x, int y)
    {
        var w = before.Image.Width; var h = before.Image.Height;
        if (after.Image.Width != w || after.Image.Height != h) return false;
        var localChanged = 0; var allChanged = 0; var all = 0;
        for (var yy = 0; yy < h; yy++) for (var xx = 0; xx < w; xx++)
        {
            var local = Math.Abs(xx - x) <= 48 && Math.Abs(yy - y) <= 48;
            if (!local && (xx % 4 != 0 || yy % 4 != 0)) continue;
            var i = (yy * w + xx) * 4;
            var delta = Math.Abs(before.Pixels[i] - after.Pixels[i]) + Math.Abs(before.Pixels[i + 1] - after.Pixels[i + 1]) + Math.Abs(before.Pixels[i + 2] - after.Pixels[i + 2]);
            if (local && delta > 45 && ++localChanged > 8) return false;
            if (xx % 4 == 0 && yy % 4 == 0) { all++; if (delta > 60) allChanged++; }
        }
        return allChanged <= Math.Max(4, all / 20);
    }
    internal static string Data(VisualObservation observation) => JsonSerializer.Serialize(new
    {
        screenshot_id = observation.Id, application = observation.Frame.Application, window = observation.Frame.Title,
        width = observation.Frame.Image.Width, height = observation.Frame.Image.Height,
        original_width = observation.Frame.Bounds.Width, original_height = observation.Frame.Bounds.Height,
        coordinates = "image pixels, origin top-left; pass x/y directly to click; do not convert to desktop coordinates",
        expires_seconds = 120
    });
    internal static (int X, int Y) ToScreen(VisualFrame frame, int x, int y) =>
        (frame.Bounds.Left + (int)((x + .5) * frame.Bounds.Width / frame.Image.Width), frame.Bounds.Top + (int)((y + .5) * frame.Bounds.Height / frame.Image.Height));
}
