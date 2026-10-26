using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace VoiceAssistant.Desktop;

public sealed partial class WindowsActions
{
    internal IVisualDriver VisualDriver(InputTarget expected) => new NativeVisualDriver(this, expected);
    sealed class NativeVisualDriver(WindowsActions owner, InputTarget expected) : IVisualDriver
    {
        public async Task<VisualFrame> CaptureAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected);
            Native.ShowWindow(expected.Window, 9); Native.SetForegroundWindow(expected.Window);
            await Task.Delay(160, token); owner.RequireInputTarget(expected);
            if (Native.GetForegroundWindow() != expected.Window) throw new AgentStoppedException("이미지로 확인할 창을 앞으로 가져오지 못했어요.");
            var bounds = VisualNative.Bounds(expected.Window);
            var title = Native.Title(expected.Window);
            using var bitmap = await VisualCapture.ReadAsync(expected, bounds, token);
            token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected);
            if (Native.GetForegroundWindow() != expected.Window || VisualNative.Bounds(expected.Window) != bounds || Native.Title(expected.Window) != title) throw new AgentStoppedException("이미지를 읽는 동안 대상 창이 바뀌었어요.");
            return VisualNative.Encode(bitmap, bounds, Native.ProcessName(expected.Window), title);
        }
        public Task ClickAsync(VisualFrame frame, int x, int y, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected);
            if (Native.GetForegroundWindow() != expected.Window || VisualNative.Bounds(expected.Window) != frame.Bounds || Native.Title(expected.Window) != frame.Title) throw new AgentStoppedException("누르기 직전에 대상 창이 바뀌었어요.");
            var point = VisualControls.ToScreen(frame, x, y);
            VisualNative.CheckPoint(expected.Window, point.X, point.Y);
            token.ThrowIfCancellationRequested(); owner.RequireInputTarget(expected);
            VisualNative.Click(expected.Window, point.X, point.Y);
            return Task.CompletedTask;
        }
    }
}

internal static class VisualNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetCursorPos(int x, int y);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

    internal static WindowPixels Bounds(IntPtr window)
    {
        if (!GetWindowRect(window, out var rect)) throw new InvalidOperationException("창의 화면 위치를 읽지 못했어요.");
        if (DwmGetWindowAttribute(window, 9, out Rect visible, Marshal.SizeOf<Rect>()) == 0 && visible.Right > visible.Left && visible.Bottom > visible.Top) rect = visible;
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width < 2 || height < 2 || width > 16000 || height > 16000 || (long)width * height > 32_000_000) throw new InvalidOperationException("이미지로 확인할 창의 크기가 올바르지 않아요.");
        var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        // GetWindowRect includes invisible resize borders. Clip those only to the
        // virtual desktop; reject a window with meaningful content off screen.
        if (rect.Left < desktop.Left - 12 || rect.Top < desktop.Top - 12 || rect.Right > desktop.Right + 12 || rect.Bottom > desktop.Bottom + 12) throw new InvalidOperationException("대상 창이 화면 밖에 있어요. 창을 화면 안으로 옮긴 뒤 다시 요청하세요.");
        var left = Math.Max(rect.Left, desktop.Left); var top = Math.Max(rect.Top, desktop.Top);
        var clippedWidth = Math.Min(rect.Right, desktop.Right) - left; var clippedHeight = Math.Min(rect.Bottom, desktop.Bottom) - top;
        if (clippedWidth < 2 || clippedHeight < 2) throw new InvalidOperationException("대상 창이 화면에 보이지 않아요.");
        return new(left, top, clippedWidth, clippedHeight);
    }
    internal static VisualFrame Encode(Bitmap original, WindowPixels bounds, string application, string title)
    {
        var scale = Math.Min(1, 1280d / Math.Max(original.Width, original.Height));
        using var resized = new Bitmap(Math.Max(1, (int)Math.Round(original.Width * scale)), Math.Max(1, (int)Math.Round(original.Height * scale)), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(resized)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(original, new Rectangle(0, 0, resized.Width, resized.Height), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel); }
        var pixels = new byte[resized.Width * resized.Height * 4];
        var locked = resized.LockBits(new Rectangle(0, 0, resized.Width, resized.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < resized.Height; y++) Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), pixels, y * resized.Width * 4, resized.Width * 4); }
        finally { resized.UnlockBits(locked); }
        using var stream = new MemoryStream(); resized.Save(stream, ImageFormat.Png); var mime = "image/png";
        if (stream.Length > 1_300_000)
        {
            stream.SetLength(0); stream.Position = 0;
            using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
            resized.Save(stream, ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters); mime = "image/jpeg";
        }
        if (stream.Length > 1_300_000) throw new InvalidOperationException("화면 이미지가 너무 복잡해 전달하지 못했어요. 창을 줄여 다시 요청하세요.");
        return new(application, title, bounds, new(mime, Convert.ToBase64String(stream.ToArray()), resized.Width, resized.Height), pixels);
    }
    internal static void CheckPoint(IntPtr target, int x, int y)
    {
        if (GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2) != target) throw new AgentStoppedException("누를 위치가 대상 창에 속하지 않아요.");
        var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        if (element == null) throw new InvalidOperationException("누를 위치의 앱 항목을 확인하지 못했어요.");
        for (var depth = 0; element != null && depth < 16; depth++, element = TreeWalker.RawViewWalker.GetParent(element))
        {
            if (element.Current.IsPassword) throw new AgentStoppedException("암호 입력란은 이미지 좌표로 조작하지 않아요.");
            if (element.Current.NativeWindowHandle == target.ToInt64()) break;
        }
    }
    internal static void Click(IntPtr target, int x, int y)
    {
        if (new[] { 1, 2, 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(Native.KeyDown)) throw new InvalidOperationException("누르고 있는 마우스 버튼이나 보조 키를 놓은 뒤 다시 요청하세요.");
        if (!GetCursorPos(out var original) || !SetCursorPos(x, y)) throw new InvalidOperationException("누를 위치로 포인터를 옮기지 못했어요.");
        try
        {
            if (Native.GetForegroundWindow() != target) throw new AgentStoppedException("누르기 직전에 대상 창이 바뀌었어요.");
            CheckPoint(target, x, y); Native.LeftClick();
        }
        finally { if (GetCursorPos(out var current) && current.X == x && current.Y == y) SetCursorPos(original.X, original.Y); }
    }
}
