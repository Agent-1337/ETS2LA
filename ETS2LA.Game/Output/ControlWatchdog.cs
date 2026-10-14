using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;

namespace ETS2LA.Game.Output;

/// <summary>Latched safety decision. Focus and perception timing are deliberately not inputs.</summary>
public sealed class ControlWatchdogPolicy
{
    public bool Latched { get; private set; }
    private long alarmAt;
    private long enabledAt;
    private bool wasEnabled, seenHealthy;
    public bool Update(long now, bool enabled, bool driving, long outputAt, long healthyAt, bool paused, bool brakeFailure = false)
    {
        if (!enabled) { Latched = false; alarmAt = 0; wasEnabled = seenHealthy = false; return false; }
        if (!wasEnabled) { enabledAt=now; wasEnabled=true; }
        if(healthyAt>0 && now-healthyAt<=600 && outputAt>0 && now-outputAt<=450) seenHealthy=true;
        if (!Latched && (brakeFailure || driving && outputAt > 0 && (seenHealthy || now-enabledAt>1000) && (now - outputAt > 450 || healthyAt == 0 || now - healthyAt > 600)))
        { Latched = true; alarmAt = now; }
        return Latched && !paused && now - alarmAt < 5000;
    }
}

/// <summary>A tiny second process can brake/pause while the main managed process is stalled in GC.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ControlWatchdog : IDisposable
{
    private readonly MemoryMappedFile memory;
    private readonly MemoryMappedViewAccessor state;
    private readonly Process process;
    public bool Ready => state.ReadInt64(24) > 0 && Environment.TickCount64 - state.ReadInt64(24) < 500;
    public bool Latched => state.ReadInt32(32) != 0;
    public ControlWatchdog(string executable, string? testNamespace = null)
    {
        string nonce=Guid.NewGuid().ToString("N");
        string name=$"Local\\ETS2LA.DrivingWatchdog.{Environment.ProcessId}.{nonce}";
        memory=MemoryMappedFile.CreateNew(name,64);
        state=memory.CreateViewAccessor();
        var info=new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--driving-watchdog"); info.ArgumentList.Add(Environment.ProcessId.ToString()); info.ArgumentList.Add(nonce);
        if(testNamespace!=null) info.ArgumentList.Add(testNamespace);
        process=Process.Start(info)??throw new InvalidOperationException("Unable to start driving watchdog");
    }
    public void Pulse(bool enabled, bool driving, long healthyAt)
    {
        if (!enabled) state.Write(40,0);
        state.Write(0,enabled?1:0); state.Write(4,driving?1:0); state.Write(16,healthyAt);
        Thread.MemoryBarrier(); state.Write(8,Environment.TickCount64);
    }
    public void RequestEmergencyStop() => state.Write(40,1);
    public void Dispose()
    {
        state.Write(0,0); state.Write(36,1);
        process.WaitForExit(1500); process.Dispose(); state.Dispose(); memory.Dispose();
    }

    public static int Run(int parentId, string nonce, string? testNamespace = null)
    {
        if (!Guid.TryParseExact(nonce,"N",out _) || parentId<=0) return 2;
        if (testNamespace!=null && !testNamespace.StartsWith("Local\\ETS2LA.Tests.",StringComparison.Ordinal)) return 2;
        try
        {
            using var parent=Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) return 2;
            using var memory=MemoryMappedFile.OpenExisting($"Local\\ETS2LA.DrivingWatchdog.{parentId}.{nonce}");
            using var state=memory.CreateViewAccessor();
            using var controls=new EmergencySdkOutput(testNamespace);
            var policy=new ControlWatchdogPolicy();
            bool wasLatched=false;
            while (state.ReadInt32(36)==0 && !parent.HasExited)
            {
                long now=Environment.TickCount64;
                state.Write(24,now);
                bool requestPause=policy.Update(now,state.ReadInt32(0)!=0,state.ReadInt32(4)!=0,state.ReadInt64(8),state.ReadInt64(16),controls.IsPaused,state.ReadInt32(40)!=0);
                state.Write(32,policy.Latched?1:0);
                if(policy.Latched) controls.Apply(requestPause);
                else if(wasLatched) controls.Clear();
                wasLatched=policy.Latched;
                Thread.Sleep(20);
            }
            // An unexpected main-process exit must not leave positive legacy pedals.
            if(parent.HasExited && state.ReadInt32(0)!=0 && state.ReadInt32(4)!=0)
                for(int i=0;i<100 && !controls.IsPaused;i++) { controls.Apply(true); Thread.Sleep(20); }
            controls.Clear();
            return 0;
        }
        catch { return 1; }
    }

    private sealed class EmergencySdkOutput : IDisposable
    {
        private MemoryMappedFile? legacyMemory, modernMemory, telemetryMemory;
        private MemoryMappedViewAccessor? legacy, modern, telemetry;
        private long retryAt;
        private bool ownsPause;
        private readonly string? testNamespace;
        public EmergencySdkOutput(string? testNamespace) => this.testNamespace=testNamespace;
        private static readonly Dictionary<string,int> offsets=Offsets();
        private static Dictionary<string,int> Offsets()
        {
            int offset=0; var result=new Dictionary<string,int>();
            foreach(var field in typeof(ControlVariables).GetFields(BindingFlags.Public|BindingFlags.Instance))
            { result[field.Name]=offset; offset+=field.FieldType==typeof(bool?)?1:4; }
            return result;
        }
        private void Connect()
        {
            if(Environment.TickCount64<retryAt) return;
            retryAt=Environment.TickCount64+1000;
            TryOpen(testNamespace==null?"Local\\SCSControls":testNamespace+".Legacy",ref legacyMemory,ref legacy);
            TryOpen(testNamespace==null?"Local\\ETS2LAPluginInput":testNamespace+".Modern",ref modernMemory,ref modern);
            TryOpen(testNamespace==null?"Local\\SCSTelemetry":testNamespace+".Telemetry",ref telemetryMemory,ref telemetry);
        }
        private static void TryOpen(string name,ref MemoryMappedFile? memory,ref MemoryMappedViewAccessor? accessor)
        {
            if(accessor!=null) return;
            try { memory=MemoryMappedFile.OpenExisting(name); accessor=memory.CreateViewAccessor(); } catch { memory?.Dispose(); memory=null; }
        }
        public bool IsPaused { get { Connect(); return telemetry?.ReadBoolean(4)??false; } }
        public void Apply(bool pause)
        {
            Connect();
            if(legacy!=null)
            {
                legacy.Write(offsets["aforward"],0f); legacy.Write(offsets["abackward"],1f);
                if(pause) { legacy.Write(offsets["pause"],true); ownsPause=true; }
                else if(ownsPause) { legacy.Write(offsets["pause"],false); ownsPause=false; }
            }
            if(modern!=null)
            {
                modern.Write(13,-1f); modern.Write(17,true); modern.Write(18,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d);
            }
        }
        public void Clear()
        {
            if(legacy!=null) { legacy.Write(offsets["aforward"],0f); legacy.Write(offsets["abackward"],0f); if(ownsPause) legacy.Write(offsets["pause"],false); }
            if(modern!=null) { modern.Write(13,0f); modern.Write(17,false); }
            ownsPause=false;
        }
        public void Dispose()
        {
            Clear(); legacy?.Dispose(); modern?.Dispose(); telemetry?.Dispose();
            legacyMemory?.Dispose(); modernMemory?.Dispose(); telemetryMemory?.Dispose();
        }
    }
}
