using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ETS2LA.Logging;

namespace ETS2LA.Controls.Windows;

/// <summary>Observes the two driving keys globally, independently of DirectInput and window focus.</summary>
internal sealed class DrivingKeyListener : IDisposable
{
    private readonly ConcurrentQueue<(string Key, bool Pressed)> pending = new();
    private readonly HookCallback callback;
    private readonly ManualResetEventSlim ready = new();
    private readonly ManualResetEventSlim stopped = new();
    private readonly Thread hookThread;
    private readonly Thread dispatchThread;
    private readonly DrivingKeyTransitions transitions;
    private uint threadId;
    private volatile bool shutdown;
    public bool IsAvailable => !shutdown && dispatchThread.IsAlive;

    public DrivingKeyListener(Action<string, bool> onKeyChanged)
    {
        transitions = new DrivingKeyTransitions(onKeyChanged, IsDown(0x26), IsDown(0x28));
        callback = ObserveKey;
        hookThread = new Thread(RunHook) { IsBackground = true, Name = "ETS2LA driving key hook" };
        dispatchThread = new Thread(DispatchKeys) { IsBackground = true, Name = "ETS2LA global driving keys" };
        dispatchThread.Start();
        hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        Logger.Info("Global driving keys active (Up/Down), independent of window focus and DirectInput.");
    }

    private IntPtr ObserveKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int key = Marshal.ReadInt32(data);
            int kind = message.ToInt32();
            if ((key == 0x26 || key == 0x28) && (kind is 0x100 or 0x101 or 0x104 or 0x105))
                pending.Enqueue((key == 0x26 ? "Up" : "Down", kind is 0x100 or 0x104));
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void DispatchKeys()
    {
        while (!shutdown)
        {
            while (!shutdown && pending.TryDequeue(out var key))
                Deliver(() => transitions.FromHook(key.Key, key.Pressed, Environment.TickCount64));

            // Windows can silently remove a low-level hook after a timeout. The
            // high bit supplies a focus-independent fallback; the low bit is unreliable.
            // Never poll inside the hook: Windows updates async state after that callback.
            if (!shutdown)
                Deliver(() => transitions.Poll(IsDown(0x26), IsDown(0x28), Environment.TickCount64));
            stopped.Wait(10);
        }
    }

    private static void Deliver(Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Error($"Failed to handle a global driving key: {ex.Message}"); }
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private void RunHook()
    {
        threadId = GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        int error = Marshal.GetLastWin32Error();
        ready.Set();
        if (hook == IntPtr.Zero)
        {
            Logger.Warn($"Could not initialize the driving key hook (Windows error {error}); global key polling remains active.");
            return;
        }
        try
        {
            while (!shutdown && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally { UnhookWindowsHookEx(hook); }
    }

    public void Dispose()
    {
        if (shutdown) return;
        shutdown = true;
        stopped.Set();
        if (threadId != 0) PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero);
        if (hookThread.Join(TimeSpan.FromSeconds(2))) ready.Dispose();
        if (Thread.CurrentThread != dispatchThread && dispatchThread.Join(TimeSpan.FromSeconds(2))) stopped.Dispose();
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
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] private static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
