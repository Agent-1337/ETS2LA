#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3DDevice = SharpDX.Direct3D11.Device;

namespace ETS2LA.Game.Perception;

/// <summary>
/// Minimal WinRT ABI bridge: CreateForWindow and a free-threaded, two-frame GPU pool.
/// GUIDs/slots follow Microsoft's Windows.Graphics.Capture metadata (windows-rs).
/// No desktop readback, injection, UI message pump, or framework retargeting.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362")]
public sealed unsafe class WindowGraphicsCapture : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private static readonly Guid ItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private readonly IntPtr window;
    private D3DDevice? device;
    private Texture2D? staging;
    private IntPtr item, pool, session, winrtDevice;
    private Size poolSize;
    private long lastFrameTime;
    private readonly object sync = new();
    private Thread? captureWorker;
    private volatile bool stopping;
    private IntPtr latest;
    public string FrameStatus { get; private set; } = "Starting game surface capture";
    private CapturedGameFrame? Unavailable(string reason) { FrameStatus = reason; return null; }

    public WindowGraphicsCapture(IntPtr gameWindow)
    {
        window = gameWindow;
        int initialized = RoInitialize(1);
        if (initialized == unchecked((int)0x80010106)) initialized = RoInitialize(0);
        Marshal.ThrowExceptionForHR(initialized);
        try
        {
            device = new D3DDevice(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using var dxgi = device.QueryInterface<SharpDX.DXGI.Device>();
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var wrapped));
            try { winrtDevice = Query(wrapped, new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670")); }
            finally { Release(ref wrapped); }
            var factory = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"));
            try
            {
                Guid iid = ItemId; IntPtr created;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(factory, 3))(factory, window, &iid, &created));
                item = created;
            }
            finally { Release(ref factory); }
            Size size;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Size*, int>)Slot(item, 7))(item, &size));
            if (size.Width < 1 || size.Height < 1) throw new InvalidOperationException("Empty game surface");
            poolSize = size;
            factory = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", new("589B103F-6BBC-5DF5-A991-02E28B3B66D5"));
            try
            {
                IntPtr created;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, Size, IntPtr*, int>)Slot(factory, 6))
                    (factory, winrtDevice, (int)Format.B8G8R8A8_UNorm, 2, size, &created));
                pool = created;
            }
            finally { Release(ref factory); }
            IntPtr createdSession;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(pool, 10))(pool, item, &createdSession));
            session = createdSession;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(session, 6))(session));
            captureWorker = new Thread(Pump) { IsBackground = true, Name = "ETS2LA.GameSurface" };
            captureWorker.Start();
        }
        catch { Dispose(); throw; }
        finally { RoUninitialize(); }
    }

    public CapturedGameFrame? CaptureClient()
    {
        lock (sync) return ReadClient();
    }

    private void Pump()
    {
        int initialized = RoInitialize(1);
        if (initialized < 0) { stopping = true; return; }
        try
        {
            while (!stopping)
            {
                try
                {
                    lock (sync)
                    {
                        if (pool == IntPtr.Zero) return;
                        for (int i = 0; i < 2; i++)
                        {
                            IntPtr next;
                            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(pool, 7))(pool, &next));
                            if (next == IntPtr.Zero) break;
                            Close(ref latest); latest = next;
                        }
                    }
                }
                catch { FrameStatus = "Game surface stopped; waiting for capture recovery"; stopping = true; }
                Thread.Sleep(25);
            }
        }
        finally { RoUninitialize(); }
    }

    private CapturedGameFrame? ReadClient()
    {
        if (stopping) throw new InvalidOperationException("Game surface capture ended");
        IntPtr frame = IntPtr.Zero;
        try
        {
            // The lightweight worker continuously drains the two-frame pool,
            // while GPU inference runs. Readback happens only on demand.
            frame = latest; latest = IntPtr.Zero;
            if (frame == IntPtr.Zero) return Unavailable("Waiting for a new game frame");
            long time;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Slot(frame, 7))(frame, &time));
            double age = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency - time / 10000d;
            if (time <= lastFrameTime || age is < -50 or > 500) return Unavailable($"Game frame stale ({age:F0} ms)");
            lastFrameTime = time;
            Size size;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Size*, int>)Slot(frame, 8))(frame, &size));
            if (size.Width != poolSize.Width || size.Height != poolSize.Height)
            {
                if (size.Width < 1 || size.Height < 1) return Unavailable("Game surface empty");
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, Size, int>)Slot(pool, 6))
                    (pool, winrtDevice, (int)Format.B8G8R8A8_UNorm, 2, size));
                poolSize = size; staging?.Dispose(); staging = null;
                return Unavailable("Game surface resizing");
            }
            if (!GetClientRect(window, out var client) || client.Right < 640 || client.Bottom < 360) return Unavailable("Game client too small");
            Point origin = new();
            if (!ClientToScreen(window, ref origin)) return Unavailable("Game client position unavailable");
            Check(DwmGetWindowAttribute(window, 9, out var bounds, Marshal.SizeOf<Rect>()));
            int left = origin.X - bounds.Left, top = origin.Y - bounds.Top;
            if (left < 0 || top < 0 || left + client.Right > size.Width || top + client.Bottom > size.Height)
                return Unavailable($"Game client resizing ({left},{top}, {client.Right}x{client.Bottom} on {size.Width}x{size.Height})");

            IntPtr surface;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(frame, 6))(frame, &surface));
            IntPtr access = IntPtr.Zero;
            try
            {
                access = Query(surface, new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"));
                Guid textureId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C"); IntPtr texture;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(access, 3))(access, &textureId, &texture));
                using var source = new Texture2D(texture);
                if (staging == null || staging.Description.Width != client.Right || staging.Description.Height != client.Bottom)
                {
                    staging?.Dispose();
                    staging = new Texture2D(device!, new Texture2DDescription
                    {
                        Width = client.Right, Height = client.Bottom, ArraySize = 1, MipLevels = 1,
                        Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                        Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read
                    });
                }
                device!.ImmediateContext.CopySubresourceRegion(source, 0,
                    new ResourceRegion(left, top, 0, left + client.Right, top + client.Bottom, 1), staging, 0);
                var mapped = device.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    using var wrapped = new Bitmap(client.Right, client.Bottom, mapped.RowPitch, PixelFormat.Format32bppArgb, mapped.DataPointer);
                    var bitmap = wrapped.Clone(new Rectangle(0, 0, client.Right, client.Bottom), PixelFormat.Format24bppRgb);
                    FrameStatus = "Fresh game surface";
                    return new(bitmap, Environment.TickCount64 - (long)Math.Max(0, age));
                }
                finally { device.ImmediateContext.UnmapSubresource(staging, 0); }
            }
            finally { Release(ref access); Release(ref surface); }
        }
        finally { Close(ref frame); }
    }

    private static IntPtr Slot(IntPtr instance, int slot) => ((IntPtr*)*(IntPtr*)instance)[slot];
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static IntPtr Query(IntPtr instance, Guid iid)
    {
        Check(Marshal.QueryInterface(instance, ref iid, out var result)); return result;
    }
    private static IntPtr Factory(string name, Guid iid)
    {
        Check(WindowsCreateString(name, name.Length, out var text));
        try { Check(RoGetActivationFactory(text, ref iid, out var result)); return result; }
        finally { WindowsDeleteString(text); }
    }
    private static void Release(ref IntPtr instance)
    {
        if (instance != IntPtr.Zero) { Marshal.Release(instance); instance = IntPtr.Zero; }
    }
    private static void Close(ref IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        IntPtr closable = IntPtr.Zero;
        try
        {
            closable = Query(instance, new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E"));
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(closable, 6))(closable);
        }
        finally { Release(ref closable); Release(ref instance); }
    }
    public void Dispose()
    {
        stopping = true;
        captureWorker?.Join(500);
        lock (sync)
        {
            Close(ref latest); Close(ref session); Close(ref pool); Release(ref item); Release(ref winrtDevice);
            staging?.Dispose(); staging = null; device?.Dispose(); device = null;
        }
    }
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr result);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int size);
}
#endif
