using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

// PrintWindow may block indefinitely inside another application. A disposable
// helper process keeps the UI responsive and lets cancellation reclaim its DCs.
// Only that window is rendered: never fall back to copying the whole desktop.
internal static class VisualCapture
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out VisualNative.Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    internal static async Task<Bitmap> ReadAsync(WindowsActions.InputTarget target, WindowPixels bounds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--capture-window");
        foreach (var value in new long[] { target.Window.ToInt64(), target.ProcessId, target.ProcessStart, bounds.Left, bounds.Top, bounds.Width, bounds.Height }) start.ArgumentList.Add(value.ToString(CultureInfo.InvariantCulture));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("창 이미지 읽기를 시작하지 못했어요.");
        try
        {
            var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
            await child.StandardOutput.BaseStream.ReadExactlyAsync(pixels, timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException("이 앱은 창 이미지 읽기를 지원하지 않아요.");
            token.ThrowIfCancellationRequested();
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(pixels, y * bitmap.Width * 4, IntPtr.Add(data.Scan0, y * data.Stride), bitmap.Width * 4); }
                finally { bitmap.UnlockBits(data); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException("대상 앱이 이미지 읽기에 응답하지 않았어요. 잠시 뒤 다시 요청하세요."); }
        catch (EndOfStreamException) { throw new InvalidOperationException("이 앱의 창 이미지를 읽지 못했어요. 창을 확인한 뒤 다시 요청하세요."); }
        finally
        {
            if (!child.HasExited) { try { child.Kill(true); } catch (InvalidOperationException) { } }
            await child.WaitForExitAsync();
        }
    }
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length != 8 || args.Skip(1).Any(x => !long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) return 2;
            var values = args.Skip(1).Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var window = new IntPtr(values[0]); var pid = checked((int)values[1]);
            var bounds = new WindowPixels(checked((int)values[3]), checked((int)values[4]), checked((int)values[5]), checked((int)values[6]));
            void Check()
            {
                Native.GetWindowThreadProcessId(window, out var currentPid);
                using var process = Process.GetProcessById(pid);
                if (currentPid != pid || process.StartTime.ToUniversalTime().Ticks != values[2] || VisualNative.Bounds(window) != bounds) throw new InvalidOperationException("Capture target changed");
            }
            Check();
            if (!GetWindowRect(window, out var rect)) return 2;
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            if (width < 2 || height < 2 || width > 16000 || height > 16000 || (long)width * height > 32_000_000) return 2;
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Magenta); var dc = graphics.GetHdc();
                try { if (!PrintWindow(window, dc, 2 /* PW_RENDERFULLCONTENT */)) return 2; }
                finally { graphics.ReleaseHdc(dc); }
            }
            Check();
            var crop = new Rectangle(bounds.Left - rect.Left, bounds.Top - rect.Top, bounds.Width, bounds.Height);
            if (!new Rectangle(0, 0, width, height).Contains(crop)) return 2;
            using var selected = bitmap.Clone(crop, PixelFormat.Format32bppArgb);
            using var output = Console.OpenStandardOutput(); var row = new byte[bounds.Width * 4];
            var data = selected.LockBits(new Rectangle(0, 0, selected.Width, selected.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < bounds.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (var i = 3; i < row.Length; i += 4) row[i] = 255;
                    output.Write(row);
                }
            }
            finally { selected.UnlockBits(data); }
            output.Flush(); return 0;
        }
        catch { return 2; }
    }
}
