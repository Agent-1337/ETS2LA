#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ETS2LA.Game.Perception;

/// <summary>Captures only the foreground game client; never captures the desktop or other apps.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class GameFrameCapture
{
    public static Bitmap? Capture(string executable)
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || IsIconic(window)) return null;
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }
        if (!GetClientRect(window, out var rect) || rect.Right < 640 || rect.Bottom < 360) return null;
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) return null;
        var bitmap = new Bitmap(rect.Right, rect.Bottom, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            // Discard a frame if focus changed while it was captured.
            if (GetForegroundWindow() != window) { bitmap.Dispose(); return null; }
            // Some presentation/HDR modes expose a blank surface to GDI. Such a
            // frame is unavailable evidence, never proof that the road is clear.
            int darkest = 255, brightest = 0;
            for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
            {
                var color = bitmap.GetPixel((int)(bitmap.Width * (0.1f + x * 0.08f)), (int)(bitmap.Height * (0.3f + y * 0.05f)));
                int luminance = (color.R + color.G * 2 + color.B) / 4;
                darkest = Math.Min(darkest, luminance); brightest = Math.Max(brightest, luminance);
            }
            if (brightest - darkest < 12) { bitmap.Dispose(); return null; }
            return bitmap;
        }
        catch { bitmap.Dispose(); return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
#endif
