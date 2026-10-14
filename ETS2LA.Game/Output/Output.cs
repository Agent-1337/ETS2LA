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
    private readonly BrakeResponseGuard brakeResponse = new();
    private volatile bool assistsEnabled;
    private string safetyStatus = "";
    private volatile DrivingEnvelope? drivingEnvelope;
    private volatile bool shutdown;
    private readonly Task outputTask;
    private long lastSteeringAt, lastPedalAt, lastPlanAt;
    private long lastBrakeDiagnostic;
    private string? lastBrakeFailure;
    #if WINDOWS
    private ControlWatchdog? watchdog;
    public void StartControlWatchdog(string executable) => watchdog ??= new ControlWatchdog(executable);
    #endif

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
        Events.Current.Subscribe<object>("Pathfinding.PlannedPathData", plan => { planGuard.Update(plan); if (planGuard.HasFreshPlan) Interlocked.Exchange(ref lastPlanAt, Environment.TickCount64); });
        Events.Current.Subscribe<DrivingEnvelope>(DrivingEnvelope.Event, envelope => drivingEnvelope = envelope);
        outputTask = Task.Factory.StartNew(Tick, TaskCreationOptions.LongRunning);
    }

    public void Shutdown()
    {
        shutdown = true;
        assistsEnabled = false;
        outputTask?.Wait(TimeSpan.FromSeconds(1));
        IsReset = false;
        ResetOutputs();
        #if WINDOWS
        watchdog?.Dispose(); watchdog = null;
        #endif
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
        long now = Environment.TickCount64;
        if (controlEvent.Variables?.steering is float steering && float.IsFinite(steering)) Interlocked.Exchange(ref lastSteeringAt, now);
        if (controlEvent.Variables != null && ControlMixer.Pedals(controlEvent.Variables) is float pedal && float.IsFinite(pedal)) Interlocked.Exchange(ref lastPedalAt, now);
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
        if (shutdown) return;
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
        if (channel.Definition.Id == "ETS2LA.Autonomy.Actions" && !assistsEnabled) { channel.BoolsProcessed = true; return; }
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
        while(!shutdown)
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

            var telemetry = GameTelemetry.Current.GetCurrentData();
            if (!assistsEnabled || !telemetry.sdkActive || telemetry.paused)
                brakeResponse.Update(Environment.TickCount64, 0, assistsEnabled, false, 0, 0, 0, 0);
            #if WINDOWS
            var guard = watchdog;
            long healthyAt = Math.Min(Math.Min(Interlocked.Read(ref lastSteeringAt), Interlocked.Read(ref lastPedalAt)),
                Math.Min(Interlocked.Read(ref lastPlanAt), GameTelemetry.Current.LastFreshFrameAt));
            if (drivingEnvelope is { } latestEnvelope) healthyAt = Math.Min(healthyAt, latestEnvelope.ValidUntil - 600);
            guard?.Pulse(assistsEnabled, telemetry.sdkActive && !telemetry.paused && Math.Abs(telemetry.truckFloat.speed) > 1, healthyAt);
            #endif

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

            if (!telemetry.sdkActive || telemetry.paused)
            {
                ResetOutputs();
                SetSafetyStatus(assistsEnabled ? brakeResponse.Failure ?? (telemetry.paused ? _("Game paused") : _("Waiting for the game SDK")) : "");
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
            else if (brakeResponse.Failure is { } failedBrake)
            {
                curFrameFloats["acceleration"].Add(new(1, -1));
                curFrameFloats["steering"].Clear();
                SetSafetyStatus(_(failedBrake));
            }
            #if WINDOWS
            else if (guard != null && (!guard.Ready || guard.Latched || Environment.TickCount64 - healthyAt > 600))
            {
                curFrameFloats["acceleration"].Add(new(1, -1f));
                curFrameFloats["steering"].Clear();
                SetSafetyStatus(guard.Latched ? _("Driving delay detected; game pause requested. Disable and enable assistance to resume.") : _("Waiting for fresh driving controls"));
            }
            #endif
            else if (!planGuard.HasFreshPlan)
            {
                curFrameFloats["acceleration"].Add(new Tuple<float, float>(1, -0.65f));
                curFrameFloats["steering"].Clear();
                SetSafetyStatus(_("Braking: route unavailable"));
            }
            else
            {
                var envelope = drivingEnvelope;
                if (envelope != null && envelope.ValidUntil >= Environment.TickCount64)
                {
                    ApplyDrivingEnvelope(envelope, telemetry.truckFloat.speed);
                    SetSafetyStatus(envelope.Reason);
                }
                else if (envelope?.RequireFresh == true)
                {
                    curFrameFloats["acceleration"].Add(new(1, -0.7f));
                    SetSafetyStatus(_("Autonomy updates lost; braking"));
                }
                else SetSafetyStatus("");
            }

            float pedal = ControlMixer.Resolve(curFrameFloats["acceleration"], brakePriority: true);
            string? brakeFailure = brakeResponse.Update(Environment.TickCount64, GameTelemetry.Current.LastFreshFrameAt,
                assistsEnabled, telemetry.sdkActive && !telemetry.paused, -pedal, telemetry.truckFloat.speed,
                telemetry.truckFloat.gameBrake, telemetry.truckFloat.gameThrottle,
                drivingEnvelope is { } brakingEnvelope && brakingEnvelope.ValidUntil >= Environment.TickCount64 ? brakingEnvelope.StoppingDistance : null);
            if (brakeFailure != null)
            {
                if (lastBrakeFailure != brakeFailure) Logger.Error(brakeFailure);
                lastBrakeFailure = brakeFailure;
                curFrameFloats["acceleration"].Add(new(1, -1));
                SetSafetyStatus(_(brakeFailure));
                #if WINDOWS
                watchdog?.RequestEmergencyStop();
                #endif
            }
            else lastBrakeFailure = null;
            if (pedal <= -0.35f && Environment.TickCount64 - lastBrakeDiagnostic >= 1000)
            {
                lastBrakeDiagnostic = Environment.TickCount64;
                Logger.Debug($"Brake response: requested={-pedal:F2}, gameBrake={telemetry.truckFloat.gameBrake:F2}, gameThrottle={telemetry.truckFloat.gameThrottle:F2}, speed={telemetry.truckFloat.speed * 3.6f:F1} km/h, reason={safetyStatus}");
            }
            WriteFloatFrame(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

            curFrameFloats.Clear();
            tickTimer.Restart();
        }

    }

    private void ApplyDrivingEnvelope(DrivingEnvelope envelope, float speed)
    {
        float brake = Math.Clamp(envelope.Brake, 0, 1);
        if (envelope.MaximumSpeed is float cap && float.IsFinite(cap) && cap >= 0)
        {
            if (speed > cap + 0.25f) brake = Math.Max(brake, Math.Clamp((speed - cap) * 0.18f + 0.05f, 0, 0.7f));
            else if (speed >= cap - 0.4f)
            {
                // A ceiling cannot dilute existing brake demands or increase throttle.
                var values = curFrameFloats["acceleration"];
                for (int i = 0; i < values.Count; i++)
                    if (values[i].Item2 > 0) values[i] = new(values[i].Item1, 0);
            }
        }
        if (brake > 0) curFrameFloats["acceleration"].Add(new(1, -brake));
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
                // Safety braking must not depend on the virtual controller's
                // per-profile input mapping. Send the same brake through both
                // SDK paths; direct input also suppresses residual throttle.
                if (weightedValue < 0)
                {
                    WriteFloat(legacy, legacyShmOffsets["aforward"], 0);
                    WriteFloat(legacy, legacyShmOffsets["abackward"], -weightedValue);
                    WriteFloat(modern, 13, weightedValue);
                    WriteDouble(modern, 18, time);
                    WriteBool(modern, 17, true);
                }
                else if (GameSettings.Current.EnableModernOutputForPedals)
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
