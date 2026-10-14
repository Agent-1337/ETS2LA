using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ETS2LA.Game;
using ETS2LA.Game.Output;
using ETS2LA.Game.Telemetry;
using ETS2LA.Settings;
using ETS2LA.Settings.Global;
using ETS2LA.State;
using ETS2LA.Backend.Plugins;
using Xunit;

namespace ETS2LA.Tests;

public sealed class DrivingRegressionTests
{
    [Theory]
    [InlineData(30)] [InlineData(50)] [InlineData(80)] [InlineData(113)]
    public void TargetIsAlwaysExactlyFiveKilometresPerHourAboveDetectedLimit(float limit)
    {
        float target = ApplicationState.SpeedTargetForLimit(limit / 3.6f);
        Assert.Equal(limit + 5, target * 3.6f, 3);
    }

    [Fact]
    public void NewSignsReplaceTheOldTargetWithoutAccumulatingOffsets()
    {
        var state = (ApplicationState)RuntimeHelpers.GetUninitializedObject(typeof(ApplicationState));
        Set(state, "stateSettings", new StateSettings { DesiredSpeedPercentageOffset = 20, DesiredSpeedStaticOffset = 10 });
        Set(state, "latestTelemetryData", new GameTelemetryData());
        foreach (float limit in new[] { 80f, 50f, 113f, 30f })
        {
            Call(state, "HandleSpeedLimitChanged", limit / 3.6f);
            Assert.Equal(limit + 5, state.DesiredSpeed * 3.6f, 3);
        }
        Call(state, "HandleSpeedLimitChanged", 0f);
        Assert.Equal(35, state.DesiredSpeed * 3.6f, 3);
        Call(state, "HandleSpeedLimitChanged", float.NaN);
        Assert.Equal(35, state.DesiredSpeed * 3.6f, 3);
    }

    [Fact]
    public void SavedSettingsCannotDisableSpeedTrackingOrCollisionConstraints()
    {
        string json = PersonalDrivingProfile.ApplyOverrides("AssistanceSettings.json", """
            {"FollowingDistance":0,"CollisionAvoidanceOption":0,"MaximumSpeed":10,"EnabledTrafficRules":[0,1]}
            """);
        var settings = JsonSerializer.Deserialize<AssistanceSettings>(json)!;
        Assert.Contains(TrafficRule.Speedlimits, settings.EnabledTrafficRules);
        Assert.Contains(TrafficRule.TrafficLights, settings.EnabledTrafficRules);
        Assert.Equal(CollisionAvoidance.Early, settings.CollisionAvoidanceOption);
        Assert.Equal(FollowingDistanceOption.Far, settings.FollowingDistance);
        Assert.Equal(AccelerationResponseOption.Fast, settings.AccelerationResponse);
        Assert.Equal(0, settings.MaximumSpeed);
        using var acc = JsonDocument.Parse(PersonalDrivingProfile.ApplyOverrides("tumppi066.adaptivecruisecontrol.json", """
            {"IgnoredConstraints":["Leading Vehicle"],"OverrideFollowingDistance":false,"FollowingDistanceOverride":1}
            """));
        Assert.Equal(0, acc.RootElement.GetProperty("IgnoredConstraints").GetArrayLength());
        Assert.Equal(3, acc.RootElement.GetProperty("FollowingDistanceOverride").GetInt32());
        Assert.Equal(8, acc.RootElement.GetProperty("StoppingDistance").GetInt32());
        Assert.Equal(2.25f, acc.RootElement.GetProperty("CenterlineTolerance").GetSingle());
    }

    [Fact]
    public void ProfilePreservesUnrelatedSettingsAndLanguage()
    {
        using var settings = JsonDocument.Parse(PersonalDrivingProfile.ApplyOverrides("StateSettings.json", """
            {"DisplayLanguage":{"Code":"fr"},"DesiredSpeedStaticOffset":0,"DesiredSpeedPercentageOffset":50}
            """));
        Assert.Equal("fr", settings.RootElement.GetProperty("DisplayLanguage").GetProperty("Code").GetString());
        Assert.Equal(5, settings.RootElement.GetProperty("DesiredSpeedStaticOffset").GetSingle() * 3.6f, 3);
        Assert.Equal(0, settings.RootElement.GetProperty("DesiredSpeedPercentageOffset").GetInt32());
        const string unrelated = "{\"ManualGamePaths\":[\"C:/Games/ATS\"]}";
        Assert.Equal(unrelated, PersonalDrivingProfile.ApplyOverrides("GameSettings.json", unrelated));
    }

    [Fact]
    public void WindowCannotRestoreOldAlwaysOnTopSetting()
    {
        using var settings = JsonDocument.Parse(PersonalDrivingProfile.ApplyOverrides("WindowSettings.json", """
            {"StayOnTop":true,"Width":1280,"Height":720,"HasCompletedOnboarding":true}
            """));
        Assert.False(settings.RootElement.GetProperty("StayOnTop").GetBoolean());
        Assert.Equal(1280, settings.RootElement.GetProperty("Width").GetInt32());
        Assert.True(settings.RootElement.GetProperty("HasCompletedOnboarding").GetBoolean());
    }

    [Fact]
    public void PluginsShareTheCurrentHostSettingsAndOutputServices()
    {
        var context = new PluginLoadContext(typeof(DrivingRegressionTests).Assembly.Location,
            Path.GetDirectoryName(typeof(DrivingRegressionTests).Assembly.Location)!);
        try
        {
            Assert.Same(typeof(SettingsHandler).Assembly, context.LoadFromAssemblyName(typeof(SettingsHandler).Assembly.GetName()));
            Assert.Same(typeof(GameOutput).Assembly, context.LoadFromAssemblyName(typeof(GameOutput).Assembly.GetName()));
        }
        finally { context.Unload(); }
    }

    [Theory]
    [InlineData(1f, 0f, 1f)]
    [InlineData(0f, 0.7f, -0.7f)]
    [InlineData(0f, -0.7f, -0.7f)]
    [InlineData(1f, 0.5f, -0.5f)]
    public void PositiveAndLegacySignedBrakeInputsNeverBecomeThrottle(float throttle, float brake, float expected)
        => Assert.Equal(expected, ControlMixer.Pedals(new() { aforward = throttle, abackward = brake }));

    [Fact]
    public void BrakeRequestWinsOverAllAccelerationWeights()
    {
        var requests = new[] { Tuple.Create(100f, 1f), Tuple.Create(1f, -0.8f), Tuple.Create(1f, -0.2f) };
        Assert.Equal(-0.8f, ControlMixer.Resolve(requests, brakePriority: true));
        Assert.Equal(-1, ControlMixer.Resolve(new[] { Tuple.Create(1f, -1f), Tuple.Create(1f, -0.65f) }, brakePriority: true));
        Assert.Equal(1, ControlMixer.Resolve(new[] { Tuple.Create(1f, 1f) }, brakePriority: true));
        Assert.Equal(0, ControlMixer.Resolve(new[] { Tuple.Create(0f, 1f), Tuple.Create(1f, float.NaN) }));
    }

    public sealed class TestPlan
    {
        public bool HasError;
        public List<int> PlannedPath = new();
    }

    [Fact]
    public void MissingEmptyFailedAndStaleRoutesAreRejected()
    {
        var guard = new DrivingPlanGuard();
        Assert.False(guard.HasFreshPlan);
        guard.Update(new TestPlan());
        Assert.False(guard.HasFreshPlan);
        guard.Update(new TestPlan { HasError = true, PlannedPath = new() { 1 } });
        Assert.False(guard.HasFreshPlan);
        guard.Update(new TestPlan { PlannedPath = new() { 1 } });
        Assert.True(guard.HasFreshPlan);
        Set(guard, "lastUpdate", Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency);
        Assert.False(guard.HasFreshPlan);
        guard.Update(null);
        Assert.False(guard.HasFreshPlan);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RealSdkBufferWritesApplyBrakesAndClearStalePedals(bool modern)
    {
        // Anonymous shared buffers exercise the production writer without connecting to a running game.
        using var legacyMap = MemoryMappedFile.CreateNew(null, 1024);
        using var modernMap = MemoryMappedFile.CreateNew(null, 26);
        using var legacy = legacyMap.CreateViewAccessor();
        using var direct = modernMap.CreateViewAccessor();
        var output = (GameOutput)RuntimeHelpers.GetUninitializedObject(typeof(GameOutput));
        Set(output, "legacyAccessor", legacy);
        Set(output, "modernAccessor", direct);
        Set(output, "legacyShmOffsets", new Dictionary<string, int> { ["aforward"] = 0, ["abackward"] = 4 });
        var floats = new Dictionary<string, List<Tuple<float, float>>> {
            ["acceleration"] = new() { Tuple.Create(100f, 1f), Tuple.Create(1f, -0.8f) }
        };
        Set(output, "curFrameFloats", floats);
        bool previous = GameSettings.Current.EnableModernOutputForPedals;
        try
        {
            GameSettings.Current.EnableModernOutputForPedals = modern;
            Call(output, "WriteFloatFrame", 1d);
            if (modern) { Assert.Equal(-0.8f, direct.ReadSingle(13)); Assert.True(direct.ReadBoolean(17)); }
            else { Assert.Equal(0, legacy.ReadSingle(0)); Assert.Equal(0.8f, legacy.ReadSingle(4)); }
            floats["acceleration"] = new() { Tuple.Create(1f, 0f) };
            floats["steering"] = new() { Tuple.Create(1f, 0f) };
            Call(output, "WriteFloatFrame", 1.5d);
            Assert.True(direct.ReadBoolean(4)); // A fresh zero steering request actively centres steering.
            Assert.Equal(modern, direct.ReadBoolean(17)); // A fresh zero pedal request means controlled coasting.
            floats.Clear();
            Call(output, "WriteFloatFrame", 2d);
            Assert.Equal(0, legacy.ReadSingle(0));
            Assert.Equal(0, legacy.ReadSingle(4));
            Assert.Equal(0, direct.ReadSingle(13));
            Assert.False(direct.ReadBoolean(17));
            Assert.False(direct.ReadBoolean(4));
        }
        finally { GameSettings.Current.EnableModernOutputForPedals = previous; }
    }

    [Fact]
    public void ExpiredAndExplicitlyRemovedChannelsDoNotLeaveAccelerationBehind()
    {
        var output = (GameOutput)RuntimeHelpers.GetUninitializedObject(typeof(GameOutput));
        output.Channels = new();
        Set(output, "channelLock", new object());
        var frame = new Dictionary<string, List<Tuple<float, float>>>();
        Set(output, "curFrameFloats", frame);
        var definition = new ControlChannelDefinition { Id = "ACC", Timeout = 0 };
        output.OnControlEvent(new() { ChannelDefinition = definition, Variables = new() { aforward = 1 } });
        Call(output, "CollectControls");
        Assert.Empty(output.Channels);
        Assert.Empty(frame);
        output.OnControlEvent(new() { ChannelDefinition = definition, Variables = null! });
        Assert.Empty(output.Channels);
    }

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Call(object target, string method, params object[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
