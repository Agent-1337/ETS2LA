using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ETS2LA.Logging;

namespace ETS2LA.Controls.Windows;

/// <summary>Observes only the two driving keys, including when the game has focus. Input is passed on unchanged.</summary>
internal sealed class DrivingKeyListener : IDisposable
{
    public ConcurrentQueue<(string Key, bool Pressed)> Pending { get; } = new();
    private readonly HookCallback callback;
    private readonly ManualResetEventSlim ready = new();
    private readonly Thread thread;
    private uint threadId;
    private volatile bool available;
    public bool IsAvailable => available;

    public DrivingKeyListener()
    {
        callback = ObserveKey;
        thread = new Thread(Run) { IsBackground = true, Name = "ETS2LA driving keys" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
    }

    private IntPtr ObserveKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int key = Marshal.ReadInt32(data);
            int kind = message.ToInt32();
            if ((key == 0x26 || key == 0x28) && (kind is 0x100 or 0x101 or 0x104 or 0x105))
                Pending.Enqueue((key == 0x26 ? "Up" : "Down", kind is 0x100 or 0x104));
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        available = hook != IntPtr.Zero;
        ready.Set();
        if (!available)
        {
            Logger.Warn($"Could not initialize driving keys (Windows error {Marshal.GetLastWin32Error()}); using DirectInput.");
            return;
        }
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally { available = false; UnhookWindowsHookEx(hook); }
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero);
        if (thread.Join(TimeSpan.FromSeconds(2))) ready.Dispose();
    }

    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Kind;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] private static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
