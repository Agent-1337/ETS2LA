using ETS2LA.Logging;
using ETS2LA.Backend.Events;
using static ETS2LA.Translations.T;

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using ETS2LA.Game.Telemetry;

namespace ETS2LA.Game.Output;

public class GameOutput
{
    private static readonly Lazy<GameOutput> _instance = new(() => new GameOutput());
    public static GameOutput Current => _instance.Value;
    public string EventString = "ETS2LA.Game.Output.ControlEvent";

    public Dictionary<string, ControlChannel> Channels = new Dictionary<string, ControlChannel>();
    private readonly object channelLock = new();
    private readonly DrivingPlanGuard planGuard = new();
    private volatile bool assistsEnabled;
    private string safetyStatus = "";

    // Example:
    // "steering" : [(0.5, 1.0), (0.2, 0.5)]
    // Where the first value is the weight and the second value is the control value.
    // The channel itself doesn't matter, just their weights.
    private Dictionary<string, List<Tuple<float, float>>> curFrameFloats = new Dictionary<string, List<Tuple<float, float>>>();
    private float TickRate = 1f / 60f;

    private Stopwatch SinceTriedMemoryAccess = new Stopwatch();
    private bool MemoryAccessAvailable => legacyAccessor != null && modernAccessor != null;
    private bool IsReset = false;

    // Legacy uses a virtual controller provided through the SCSControls plugin. This
    // system works the same as SteamInput for example.

    string legacyMapName = "Local\\SCSControls";
    string legacyMapNameLinux = "/dev/shm/SCS/SCSControls";
    int legacyMapSize = 0;
    Dictionary<string, int> legacyShmOffsets = new Dictionary<string, int>();
    MemoryMappedFile? legacyMmf = null;
    MemoryMappedViewAccessor? legacyAccessor = null;

    // Modern uses ETS2LAPlugin to write directly to the game's memory. This does
    // not however work on all devices, and it doesn't provide such an extensive list
    // of controls as the legacy system does.

    string modernMapName = "Local\\ETS2LAPluginInput";
    string modernMapNameLinux = "/dev/shm/ETS2LAPluginInput";
    int modernMapSize = 26;
    MemoryMappedFile? modernMmf = null;
    MemoryMappedViewAccessor? modernAccessor = null;

    public GameOutput()
    {
        // Calculate the memory offsets for each of the variables
        // for legacy mode.
        int boolSize = sizeof(bool);
        int floatSize = sizeof(float);

        int offset = 0;
        foreach (var field in typeof(ControlVariables).GetFields())
        {
            legacyShmOffsets[field.Name] = offset;
            if (field.FieldType == typeof(bool?))
                offset += boolSize;
            else if (field.FieldType == typeof(float?))
                offset += floatSize;
        }
        legacyMapSize = offset;

        SinceTriedMemoryAccess.Start();
        TryOpenMemory();
        // Then we start listening to events and spin up the update thread.
        Events.Current.Subscribe<ControlEvent>(EventString, OnControlEvent);
        Events.Current.Subscribe<bool>("ETS2LA.State.AssistsEnabled", enabled => assistsEnabled = enabled);
        Events.Current.Subscribe<object>("Pathfinding.PlannedPathData", planGuard.Update);
        Task.Factory.StartNew(Tick, TaskCreationOptions.LongRunning);
    }

    private void TryOpenMemory()
    {
        if (MemoryAccessAvailable)
            return;

        float secondsSinceLastTry = (float)SinceTriedMemoryAccess.Elapsed.TotalSeconds;
        if (secondsSinceLastTry < 5f)
            return;

        try
        {
            #if WINDOWS
                legacyMmf = MemoryMappedFile.OpenExisting(legacyMapName);
                modernMmf = MemoryMappedFile.OpenExisting(modernMapName);
            # else
                legacyMmf = MemoryMappedFile.CreateFromFile(legacyMapNameLinux);
                modernMmf = MemoryMappedFile.CreateFromFile(modernMapNameLinux);
            # endif
            legacyAccessor = legacyMmf.CreateViewAccessor(0, legacyMapSize, MemoryMappedFileAccess.Write);
            modernAccessor = modernMmf.CreateViewAccessor(0, modernMapSize, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            // Logging.Logger.Error("Failed to open memory: " + ex.Message);
            legacyAccessor = null;
            modernAccessor = null;
        }

        Logging.Logger.Debug(MemoryAccessAvailable ? _("Successfully opened memory for output.")
                                                  : _("Memory not available for output, please ensure the game is running and reinstall the SDK if necessary."));
        SinceTriedMemoryAccess.Restart();
    }

    public void OnControlEvent(ControlEvent controlEvent)
    {
        lock (channelLock)
        {
        string channel = controlEvent.ChannelDefinition.Id;
        if (controlEvent.Variables == null || controlEvent.Properties == null)
        {
            Channels.Remove(channel);
            return;
        }
        if (!Channels.ContainsKey(channel))
        {
            Channels[channel] = new ControlChannel{
                Definition = controlEvent.ChannelDefinition,
                Properties = controlEvent.Properties,
                Variables = controlEvent.Variables
            };
        }
        else if (controlEvent.Variables == null || controlEvent.Properties == null)
        {
            Channels.Remove(channel);
        }
        else
        {
            Channels[channel].Properties = controlEvent.Properties;
            Channels[channel].Variables = controlEvent.Variables;
            Channels[channel].LastUpdate.Restart();
            Channels[channel].BoolsProcessed = false;
        }
        }
    }

    private void ResetOutputs()
    {
        if (!MemoryAccessAvailable)
            return;

        if (IsReset)
            return;

        if (legacyAccessor != null)
        {
            foreach (var field in typeof(ControlVariables).GetFields())
            {
                if (field.FieldType == typeof(float?))
                {
                    WriteFloat(legacyAccessor!, legacyShmOffsets[field.Name], 0);
                }
                else if (field.FieldType == typeof(bool?))
                {
                    WriteBool(legacyAccessor!, legacyShmOffsets[field.Name], false);
                }
            }
            legacyAccessor.Flush();
        }

        if(modernAccessor != null)
        {
            WriteFloat(modernAccessor, 0, 0);
            WriteBool(modernAccessor, 4, false);
            WriteDouble(modernAccessor, 5, 0);
            WriteFloat(modernAccessor, 13, 0);
            WriteBool(modernAccessor, 17, false);
            WriteDouble(modernAccessor, 18, 0);

            modernAccessor.Flush();
        }

        IsReset = true;
        Logging.Logger.Debug("Reset outputs to default values.");
    }

    private void ToggleBool(MemoryMappedViewAccessor accessor, int offset)
    {
        WriteBool(accessor, offset, true);
        Thread.Sleep(50);
        WriteBool(accessor, offset, false);
    }

    private void WriteBool(MemoryMappedViewAccessor accessor, int offset, bool value)
    {
        accessor.Write(offset, value);
    }

    private void WriteFloat(MemoryMappedViewAccessor accessor, int offset, float value)
    {
        accessor.Write(offset, value);
    }

    private void WriteDouble(MemoryMappedViewAccessor accessor, int offset, double value)
    {
        accessor.Write(offset, value);
    }

    private void ProcessChannel(ControlChannel channel)
    {
        var boolType = channel.Properties.BooleanType;
        var weight = channel.Properties.Weight;
        float? pedals = ControlMixer.Pedals(channel.Variables);
        if (pedals != null)
        {
            if (!curFrameFloats.ContainsKey("acceleration")) curFrameFloats["acceleration"] = new();
            curFrameFloats["acceleration"].Add(new Tuple<float, float>(weight, pedals.Value));
        }

        foreach (var prop in typeof(ControlVariables).GetFields())
        {
            var value = prop.GetValue(channel.Variables);
            if (prop.Name is "aforward" or "abackward") continue;
            if (value != null)
            {
                if (prop.FieldType == typeof(bool?) && !channel.BoolsProcessed)
                {
                    bool boolValue = (bool)value;
                    if(boolValue && boolType == ControlBooleanType.TrueToToggle)
                        Task.Run(() => ToggleBool(legacyAccessor!, legacyShmOffsets[prop.Name]));
                    else
                        WriteBool(legacyAccessor!, legacyShmOffsets[prop.Name], boolValue);
                }

                if (prop.FieldType == typeof(float?))
                {
                    float floatValue = (float)value;

                    string propName = prop.Name;
                    if (propName == "aforward" || propName == "abackward")
                        propName = "acceleration";

                    if (!curFrameFloats.ContainsKey(propName))
                        curFrameFloats[propName] = new List<Tuple<float, float>>();

                    curFrameFloats[propName].Add(new Tuple<float, float>(weight, floatValue));
                }
            }
        }

        channel.BoolsProcessed = true;
    }

    public void Tick()
    {
        Stopwatch tickTimer = Stopwatch.StartNew();
        while(true)
        {
            double timeLeft = TickRate - tickTimer.Elapsed.TotalSeconds;
            if (timeLeft > 0 && timeLeft < TickRate)
            {
                if (timeLeft * 1000 > 0.5)
                {
                    Thread.Sleep((int)(timeLeft * 1000));
                    continue;
                }
            }

            // These || need to be added to silence warnings...
            // If someone knows how to make the compiler understand that MemoryAccessAvailable ensures
            // that the accessors are not null, then please tell me.
            if (!MemoryAccessAvailable || legacyAccessor == null || modernAccessor == null)
            {
                TryOpenMemory();
                SetSafetyStatus(assistsEnabled ? _("Waiting for the game SDK") : "");
                tickTimer.Restart();
                continue;
            }

            var telemetry = GameTelemetry.Current.GetCurrentData();
            if (!telemetry.sdkActive || telemetry.paused)
            {
                ResetOutputs();
                SetSafetyStatus(assistsEnabled ? _("Waiting for the game SDK") : "");
                tickTimer.Restart();
                continue;
            }

            IsReset = false;
            CollectControls();

            // Clear pedals and steering every frame that lacks a fresh request.
            // Legacy pedal values have no timestamp watchdog in the game.
            curFrameFloats.TryAdd("acceleration", new());
            curFrameFloats.TryAdd("steering", new());
            if (!assistsEnabled)
            {
                curFrameFloats["acceleration"].Clear();
                curFrameFloats["steering"].Clear();
                SetSafetyStatus("");
            }
            else if (!planGuard.HasFreshPlan)
            {
                curFrameFloats["acceleration"].Add(new Tuple<float, float>(1, -0.65f));
                curFrameFloats["steering"].Clear();
                SetSafetyStatus(_("Braking: route unavailable"));
            }
            else SetSafetyStatus("");

            WriteFloatFrame(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

            curFrameFloats.Clear();
            tickTimer.Restart();
        }

    }

    private void CollectControls()
    {
        curFrameFloats.Clear();
        try
        {
            lock (channelLock)
            foreach (var channel in Channels.Values.ToArray())
            {
                if (channel.Properties == null || channel.Variables == null)
                    continue;

                if (channel.LastUpdate.Elapsed.TotalSeconds > channel.Definition.Timeout)
                {
                    Channels.Remove(channel.Definition.Id);
                    continue;
                }

                ProcessChannel(channel);
            }
        } catch (Exception ex) { Logger.Error(_("Failed to process control outputs: {0}", ex.Message)); }
    }

    private void WriteFloatFrame(double time)
    {
        if (modernAccessor is not { } modern || legacyAccessor is not { } legacy) return;
        curFrameFloats.TryAdd("acceleration", new());
        curFrameFloats.TryAdd("steering", new());
        foreach (var kvp in curFrameFloats)
        {
            string propName = kvp.Key;
            List<Tuple<float, float>> values = kvp.Value;

            bool hasRequest = values.Any(v => float.IsFinite(v.Item1) && v.Item1 > 0 && float.IsFinite(v.Item2));
            float weightedValue = ControlMixer.Resolve(values, brakePriority: propName == "acceleration");

            if(propName == "steering")
            {
                WriteFloat(modern, 0, weightedValue);
                WriteBool(modern, 4, hasRequest);
                WriteDouble(modern, 5, time);
            }
            else if (propName == "acceleration")
            {
                if (GameSettings.Current.EnableModernOutputForPedals)
                {
                    WriteFloat(legacy, legacyShmOffsets["aforward"], 0);
                    WriteFloat(legacy, legacyShmOffsets["abackward"], 0);
                    WriteFloat(modern, 13, weightedValue);
                    WriteBool(modern, 17, hasRequest);
                    WriteDouble(modern, 18, time);
                }
                else
                {
                    WriteFloat(modern, 13, 0);
                    WriteBool(modern, 17, false);
                    if (weightedValue > 0)
                    {
                        WriteFloat(legacy, legacyShmOffsets["aforward"], weightedValue);
                        WriteFloat(legacy, legacyShmOffsets["abackward"], 0);
                    }
                    else
                    {
                        WriteFloat(legacy, legacyShmOffsets["abackward"], -weightedValue);
                        WriteFloat(legacy, legacyShmOffsets["aforward"], 0);
                    }
                }
            }
            else
            {
                WriteFloat(legacy, legacyShmOffsets[propName], weightedValue);
            }
        }

        modern.Flush();
        legacy.Flush();

    }

    private void SetSafetyStatus(string status)
    {
        if (safetyStatus == status) return;
        safetyStatus = status;
        Events.Current.Publish("ETS2LA.Game.Output.SafetyStatus", status);
    }
}
