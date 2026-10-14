#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ETS2LA.Game.Perception;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed record CapturedGameFrame(Bitmap Image, long CapturedAt) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>Captures the selected game's own GPU surface, independently of focus or occlusion.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362")]
public sealed class GameFrameCapture : IDisposable
{
    private WindowGraphicsCapture? capture;
    private string? selectedExecutable;
    private IntPtr window;
    private uint owner;
    private long retryAt;
    public string Status { get; private set; } = "Waiting for game frames";

    public CapturedGameFrame? Capture(string executable)
    {
        try
        {
            GetWindowThreadProcessId(window, out uint currentOwner);
            if (!string.Equals(selectedExecutable, executable, StringComparison.OrdinalIgnoreCase) ||
                window != IntPtr.Zero && (!IsWindow(window) || currentOwner != owner)) Reset();
            if (capture == null)
            {
                if (Environment.TickCount64 < retryAt) return null;
                window = FindWindow(executable);
                if (window == IntPtr.Zero) { Status = "Waiting for the selected game window"; return null; }
                selectedExecutable = executable;
                GetWindowThreadProcessId(window, out owner);
                capture = new WindowGraphicsCapture(window);
            }
            if (IsIconic(window)) { Status = "Game minimized; visual maneuvers suspended"; return null; }
            var result = capture.CaptureClient();
            if (result != null && !HasSceneContrast(result.Image))
            {
                result.Dispose(); Status = "Game surface blank; visual maneuvers suspended"; return null;
            }
            Status = result == null ? capture.FrameStatus + "; visual maneuvers suspended" : "Capturing the game surface (Windows Graphics Capture)";
            return result;
        }
        catch (Exception ex)
        {
            Reset(); retryAt = Environment.TickCount64 + 2000;
            Status = $"Game surface unavailable; visual maneuvers suspended ({ex.GetType().Name})";
            return null;
        }
    }

    private static IntPtr FindWindow(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            try
            {
                if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase) &&
                    process.MainWindowHandle != IntPtr.Zero && IsWindow(process.MainWindowHandle)) return process.MainWindowHandle;
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private static bool HasSceneContrast(Bitmap image)
    {
        int darkest = 255, brightest = 0;
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 10; x++)
        {
            var color = image.GetPixel((int)(image.Width * (0.1f + x * 0.08f)), (int)(image.Height * (0.3f + y * 0.05f)));
            int luminance = (color.R + color.G * 2 + color.B) / 4;
            darkest = Math.Min(darkest, luminance); brightest = Math.Max(brightest, luminance);
        }
        return brightest - darkest >= 12;
    }

    private void Reset() { capture?.Dispose(); capture = null; window = IntPtr.Zero; owner = 0; selectedExecutable = null; }
    public void Dispose() => Reset();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
#endif
