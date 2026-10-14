using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ETS2LA.Game;
using ETS2LA.Game.Output;
using ETS2LA.Game.Perception;
using ETS2LA.State.Autonomy;
using Xunit;

namespace ETS2LA.Tests;

public sealed class RoadblockRegressionTests
{
    private static DrivingScene Scene(float distance, float speed = 42 / 3.6f, float rigLength = 45)
    {
        var road = new RouteCorridor(Enumerable.Range(-20, 141).Select(i => new Vector3(0, 0, i * 3)).ToArray(), 1, 2, 0, -3.5f, 0, false);
        var obstacle = new SceneObstacle("vision:block", "barricade", new(0, 0, distance), 0.6f, 0, 0.9f, 2, Confirmed: true);
        return new(1000, true, true, Vector3.Zero, speed, rigLength, road, [obstacle], true, true, false, false, false, null, 0, false, SpeedLimit: 121 / 3.6f);
    }

    [Fact]
    public void FortyTwoKmhRoadblockWritesRealBrakingThroughBothSdkPathsDespiteCruiseThrottle()
    {
        var decision = new ManeuverPlanner().Decide(Scene(32));
        Assert.True(decision.Brake >= 0.65f);
        Assert.Equal(0, decision.Signal);
        Assert.DoesNotContain("Stopped", decision.Status);

        using var legacyMap = MemoryMappedFile.CreateNew(null, 1024);
        using var modernMap = MemoryMappedFile.CreateNew(null, 26);
        using var legacy = legacyMap.CreateViewAccessor();
        using var modern = modernMap.CreateViewAccessor();
        var output = (GameOutput)RuntimeHelpers.GetUninitializedObject(typeof(GameOutput));
        Set(output, "legacyAccessor", legacy); Set(output, "modernAccessor", modern);
        // Fixed offsets from the SCSControls wire ABI, rather than invented test offsets.
        Set(output, "legacyShmOffsets", new Dictionary<string, int> { ["aforward"] = 122, ["abackward"] = 126 });
        Set(output, "curFrameFloats", new Dictionary<string, List<Tuple<float, float>>> { ["acceleration"] = [new(100, 1)] });
        bool previous = GameSettings.Current.EnableModernOutputForPedals;
        try
        {
            GameSettings.Current.EnableModernOutputForPedals = false;
            Call(output, "ApplyDrivingEnvelope", new DrivingEnvelope(1600, decision.Brake, decision.SpeedCap, decision.Status), Scene(32).Speed);
            Call(output, "WriteFloatFrame", 12d);
            Assert.Equal(0, legacy.ReadSingle(122));
            Assert.InRange(legacy.ReadSingle(126), 0.65f, 1);
            Assert.Equal(-legacy.ReadSingle(126), modern.ReadSingle(13));
            Assert.True(modern.ReadBoolean(17)); Assert.Equal(12, modern.ReadDouble(18));
        }
        finally { GameSettings.Current.EnableModernOutputForPedals = previous; }
    }

    [Fact]
    public void ATemporaryFreshButEmptyObservationCannotReleaseRoadblockBraking()
    {
        var planner = new ManeuverPlanner();
        var scene = Scene(25);
        Assert.True(planner.Decide(scene).Brake > 0.65f);
        var missed = planner.Decide(scene with { Now = 1200, Obstacles = [], VisionFresh = true });
        Assert.True(missed.Brake > 0.65f);
        Assert.NotNull(missed.SpeedCap);
        var moved = planner.Decide(scene with { Now = 1300, Obstacles = [scene.Obstacles[0] with { Speed = 8 }] });
        Assert.Null(moved.SpeedCap);
        Assert.Equal(0, moved.Brake);
        Assert.Null(planner.Decide(scene with { Now = 1400, Obstacles = [] }).SpeedCap);
    }

    [Fact]
    public void RememberingAConeClosureRetainsItsClusterEvidenceThroughPartialAndMissingFrames()
    {
        var scene = Scene(25);
        var markers = Enumerable.Range(0, 3).Select(i => new SceneObstacle($"vision:cone:{i}", "traffic cone", new(0, 0, 25 + i * 2), 0.3f, 0, 0.8f, Confirmed: true)).ToArray();
        scene = scene with { Obstacles = markers };
        var planner = new ManeuverPlanner();
        Assert.True(planner.Decide(scene).Brake > 0.65f);
        Assert.True(planner.Decide(scene with { Now = 1200, Obstacles = [markers[0]] }).Brake > 0.65f);
        Assert.True(planner.Decide(scene with { Now = 1400, Obstacles = [] }).Brake > 0.65f);
    }

    [Fact]
    public void ArticulationAndExistingRoadCurvatureAreIncludedInMergeClearance()
    {
        var scene = Scene(135, 10);
        var articulated = scene with { Rig = new(45, 1.3f, [], 0.4f, 1) };
        Assert.Equal(0, new ManeuverPlanner().Decide(articulated).Signal);
        const float radius = 180;
        var curve = scene.Route with { Points = Enumerable.Range(-20, 141).Select(i => {
            float angle = i * 3 / radius; return new Vector3(radius * (1 - MathF.Cos(angle)), 0, radius * MathF.Sin(angle));
        }).ToArray() };
        var curved = scene with { Route = curve, Obstacles = [scene.Obstacles[0] with { Position = curve.PointAt(curve.Project(Vector3.Zero).Along + 135) }] };
        Assert.True(LaneChangeClearance.RequiredDistance(curved, curved.Obstacles[0], -3.5f) >
            LaneChangeClearance.RequiredDistance(scene, scene.Obstacles[0], -3.5f));
    }

    [Fact]
    public void ClearanceUsesTheCubicTrajectoryWithoutAddingTheEntireConvoyTwice()
    {
        var scene = Scene(135, speed: 10, rigLength: 60);
        Assert.True(135 < LaneChangeClearance.NativeDistance(scene) + scene.RigLength + 12); // Previously refused.
        var decision = new ManeuverPlanner().Decide(scene);
        Assert.Equal(-1, decision.Signal);
        Assert.Equal(0, decision.Brake);
        Assert.Equal(0, new ManeuverPlanner().Decide(Scene(32)).Signal); // Shortcuts still cannot cut through the block.
        Assert.Equal(0, new ManeuverPlanner().Decide(scene with { Obstacles = [scene.Obstacles[0] with { Radius = 2.4f }] }).Signal);
    }

    [Fact]
    public void ALongTrailerNeedsMoreClearanceAndARearApproachStillPreventsAMerge()
    {
        var scene = Scene(135, 10);
        var shortRig = scene with { Rig = new(18, 1.3f, [], 0, 0, TrackingSpan: 8) };
        var longRig = scene with { Rig = new(60, 1.3f, [], 0, 2, TrackingSpan: 24) };
        Assert.True(LaneChangeClearance.RequiredDistance(longRig, scene.Obstacles[0], -3.5f) >
            LaneChangeClearance.RequiredDistance(shortRig, scene.Obstacles[0], -3.5f));
        var occupied = scene with { Obstacles = [scene.Obstacles[0], new("traffic:rear", "car", new(-3.5f, 0, -80), 1, 25, 1, 5)] };
        Assert.Equal(0, new ManeuverPlanner().Decide(occupied).Signal);
        var stoppedAhead = scene with { Obstacles = [scene.Obstacles[0], new("traffic:ahead", "car", new(-3.5f, 0, 130), 1, 0, 1, 5)] };
        Assert.Equal(0, new ManeuverPlanner().Decide(stoppedAhead).Signal);
        Assert.Equal(-1, new ManeuverPlanner().Decide(stoppedAhead with { Obstacles = [scene.Obstacles[0], stoppedAhead.Obstacles[1] with { Speed = 15 }] }).Signal);
        Assert.Equal(0, new ManeuverPlanner().Decide(longRig with { Rig = longRig.Rig! with { HasLiveTail = false } }).Signal);
    }

    [Theory]
    [InlineData(10, 1)] [InlineData(18, 1)] [InlineData(24, 1)] [InlineData(18, 2)] [InlineData(24, 2)]
    public void AcceptedNativeClearanceAlsoClearsAKinematicArticulatedConvoy(float wheelbase, int trailers)
    {
        var scene = Scene(150, 10, 12 + trailers * (wheelbase + 6));
        scene = scene with { Rig = new(scene.RigLength, 1.3f, [], 0, trailers, TrackingSpan: MathF.Sqrt(64 + trailers * wheelbase * wheelbase)) };
        float length = LaneChangeClearance.NativeDistance(scene);
        float obstruction = LaneChangeClearance.RequiredDistance(scene, scene.Obstacles[0], -3.5f) + 6;
        Assert.True(float.IsFinite(obstruction));
        var axles = Enumerable.Range(0, trailers).Select(i => new Vector2(0, -8 - wheelbase - i * (wheelbase + 6))).ToArray();
        for (float s = 0; s < length + scene.RigLength + 50; s += 0.25f)
        {
            var tractor = Path(s);
            var forward = Vector2.Normalize(Path(s + 0.01f) - Path(s));
            CheckBody(tractor, forward, 4, -8);
            var hook = tractor - forward * 8;
            for (int i = 0; i < trailers; i++)
            {
                // No-slip trailing axle follows its moving hitch. This independent
                // simulation checks the complete body crossing the obstacle slab.
                var trailerForward = Vector2.Normalize(hook - axles[i]);
                axles[i] = hook - wheelbase * trailerForward;
                CheckBody(hook, trailerForward, 1, -wheelbase - 6);
                hook = axles[i] - trailerForward * 6;
            }
        }

        Vector2 Path(float s)
        {
            float t = Math.Clamp(s / length, 0, 1);
            return new(-3.5f * t * t * (3 - 2 * t), s);
        }
        void CheckBody(Vector2 origin, Vector2 forward, float front, float rear)
        {
            var side = new Vector2(-forward.Y, forward.X) * 1.3f;
            Vector2[] corners = [origin + forward * front + side, origin + forward * front - side,
                origin + forward * rear - side, origin + forward * rear + side];
            for (int i = 0; i < 4; i++)
            {
                var a = corners[i]; var b = corners[(i + 1) % 4];
                if (Math.Abs(a.Y - obstruction) <= 1) Assert.True(a.X < -0.7f);
                foreach (float edge in new[] { obstruction - 1, obstruction + 1 })
                    if ((a.Y - edge) * (b.Y - edge) < 0)
                        Assert.True(a.X + (b.X - a.X) * (edge - a.Y) / (b.Y - a.Y) < -0.7f);
            }
        }
    }

    [Fact]
    public void StopSimulationHoldsTheTruckBeforeAConfirmedRoadblockThroughDroppedObservations()
    {
        var scene = Scene(32);
        var planner = new ManeuverPlanner();
        float speed = scene.Speed, position = 0;
        for (int i = 0; i < 140; i++)
        {
            var decision = planner.Decide(scene with { Now = 1000 + i * 100, Speed = speed, Ego = new(0, 0, position), Obstacles = i > 1 ? [] : scene.Obstacles });
            Assert.Equal(0, decision.Signal);
            float pedal = decision.Brake > 0 ? -decision.Brake : speed > decision.SpeedCap ? -0.7f : 1;
            speed = Math.Max(0, speed + pedal * (pedal < 0 ? 4 : 1) * 0.1f);
            position += speed * 0.1f;
            Assert.True(position < 23, $"Truck crossed the stand-off distance at {position:F1}m");
        }
        Assert.InRange(speed, 0, 0.01f);
    }

    [Fact]
    public void MissingBrakeAcknowledgmentLatchesEvenWhenEveryControllerIsFresh()
    {
        var guard = new BrakeResponseGuard();
        Assert.Null(guard.Update(1000, 1000, true, true, 0.8f, 12, 0, 1));
        Assert.Null(guard.Update(1400, 1400, true, true, 0.8f, 12, 0, 1));
        Assert.NotNull(guard.Update(1460, 1460, true, true, 0.8f, 12, 0, 1));
        Assert.NotNull(guard.Update(1500, 1500, true, true, 0, 12, 0, 0));
        Assert.Null(guard.Update(1600, 1600, false, true, 0, 12, 0, 0));
        var policy = new ControlWatchdogPolicy();
        Assert.True(policy.Update(2000, true, true, 2000, 2000, false, brakeFailure: true));
        Assert.True(policy.Latched);
        Assert.False(policy.Update(2020, true, true, 2020, 2020, true));
        Assert.True(policy.Latched);
    }

    [Fact]
    public void AppliedBrakesMustActuallySlowTheTruckAndFrozenFramesAreNotRepeatedEvidence()
    {
        var guard = new BrakeResponseGuard();
        guard.Update(1000, 1000, true, true, 0.8f, 12, 0.8f, 0);
        Assert.Null(guard.Update(2500, 1000, true, true, 0.8f, 12, 0, 1));
        Assert.NotNull(guard.Update(2520, 2520, true, true, 0.8f, 12, 0.8f, 0));
        var working = new BrakeResponseGuard();
        for (int i = 0; i <= 30; i++)
            Assert.Null(working.Update(1000 + i * 100, 1000 + i * 100, true, true, 0.8f, 12 - i * 0.3f, 0.8f, 0));
        Assert.Null(new BrakeResponseGuard().Update(3000, 3000, true, false, 1, 12, 0, 1));
    }

    [Fact]
    public void AcknowledgedButWeakBrakingCannotOverrunTheAvailableStoppingDistance()
    {
        var weak = new BrakeResponseGuard();
        weak.Update(1000, 1000, true, true, 0.8f, 12, 0.8f, 0, 22);
        Assert.NotNull(weak.Update(1520, 1520, true, true, 0.8f, 11.5f, 0.8f, 0, 16));
        var sufficient = new BrakeResponseGuard();
        sufficient.Update(1000, 1000, true, true, 0.8f, 12, 0.8f, 0, 22);
        Assert.Null(sufficient.Update(1520, 1520, true, true, 0.8f, 9.9f, 0.8f, 0, 16));
    }

    private static void Set(object value, string field, object data) => typeof(GameOutput).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, data);
    private static void Call(object value, string method, params object[] args) => typeof(GameOutput).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
}
