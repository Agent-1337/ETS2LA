#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ETS2LA.Game.Perception;

/// <summary>Reusable image buffers for the single perception worker. Never shared between workers.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PixelWorkspace : IDisposable
{
    private Bitmap? scaled;
    public byte[] Pixels { get; private set; } = [];
    public byte[] Orange { get; private set; } = [];
    public byte[] Red { get; private set; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public void Read(Bitmap image, int maximumWidth, Rectangle? region = null)
    {
        var source = region ?? new Rectangle(0,0,image.Width,image.Height);
        int width = Math.Min(maximumWidth, source.Width), height = source.Height * width / source.Width;
        if (scaled == null || width != Width || height != Height)
        {
            scaled?.Dispose(); scaled = new Bitmap(width,height,PixelFormat.Format24bppRgb);
            Width=width; Height=height; Stride=(width*3+3)&~3;
            Pixels=new byte[Stride*height]; Orange=new byte[width*height]; Red=new byte[width*height];
        }
        else { Array.Clear(Orange); Array.Clear(Red); }
        using(var graphics=Graphics.FromImage(scaled)) graphics.DrawImage(image,new Rectangle(0,0,width,height),source,GraphicsUnit.Pixel);
        var locked=scaled.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
        try { Marshal.Copy(locked.Scan0,Pixels,0,Pixels.Length); }
        finally { scaled.UnlockBits(locked); }
    }
    public void Dispose() { scaled?.Dispose(); scaled=null; }
}
#endif
