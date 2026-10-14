using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ETS2LA.Game.Output;
using ETS2LA.Game.Perception;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Shared;
using ETS2LA.State.Autonomy;
using Xunit;

namespace ETS2LA.Tests;

public sealed class AutonomyTests
{
    [Fact]
    public void WindowsBuildIncludesTheLiveVisionWorker()
    {
        if (OperatingSystem.IsWindows()) Assert.NotNull(typeof(AutonomySupervisor).GetMethod("VisionLoop", BindingFlags.Instance | BindingFlags.NonPublic));
    }
    private static RouteCorridor Road(int lane = 1) => new(Enumerable.Range(-20, 121).Select(i => new Vector3(lane == 1 ? 0 : -3.5f, 0, i * 3)).ToArray(), lane,
        lane == 1 ? 2 : 0, lane == 1 ? 0 : 1, -3.5f, 3.5f, false);
    private static SceneObstacle Barrier(float distance = 190) => new("vision:1", "barricade", new(0, 0, distance), 0.5f, 0, 0.75f);
    private static DrivingScene Scene(long now = 1000) => new(now, true, true, Vector3.Zero, 5, 26, Road(), [Barrier()], true, true, false, false, false, null, 0, false);

    [Fact]
    public void PassingStartsOnlyAfterConfirmationInAClearSameDirectionLane()
    {
        var planner = new ManeuverPlanner();
        Assert.Equal(0, planner.Decide(Scene()).Signal);
        var move = planner.Decide(Scene(1800));
        Assert.Equal(-1, move.Signal);
        Assert.True(move.SpeedCap <= Scene().SpeedLimit + 5 / 3.6f);
        Assert.Equal(0, planner.Decide(Scene(2000)).Signal); // Never pulse repeatedly while changing lanes.
    }

    [Fact]
    public void FastApproachSlowsBeforeInitiatingAManeuver()
    {
        var planner = new ManeuverPlanner();
        var decision = planner.Decide(Scene() with { Speed = 25 });
        Assert.Equal(0, decision.Signal);
        Assert.True(decision.SpeedCap < 25);
        Assert.True(decision.SpeedCap > 5.56f);
    }

    [Theory]
    [InlineData(-25f, 30f)]
    [InlineData(15f, 0f)]
    public void ARearApproachOrVehicleAheadInAdjacentLaneBlocksPassing(float position, float speed)
    {
        var planner = new ManeuverPlanner();
        var scene = Scene() with { Obstacles = [Barrier(), new("traffic:2", "vehicle", new(-3.5f, 0, position), 1, speed, 1, 5)] };
        planner.Decide(scene);
        Assert.Equal(0, planner.Decide(scene with { Now = 1800 }).Signal);
    }

    [Fact]
    public void FastRearTrafficIsCheckedOverTheWholeNativeLaneChangeDuration()
    {
        var planner = new ManeuverPlanner();
        var scene = Scene() with { Obstacles = [Barrier(), new("traffic:2", "vehicle", new(-3.5f, 0, -210), 1, 30, 1, 5)] };
        planner.Decide(scene);
        Assert.Equal(0, planner.Decide(scene with { Now = 1800 }).Signal);
        Assert.True(scene.Route.Project(new(0, 0, -210)).Along < scene.Route.Project(new(0, 0, -60)).Along);
    }

    [Fact]
    public void RoadsideVehiclesOutsideTheSweptLaneDoNotBlockCruising()
    {
        var decision = new ManeuverPlanner().Decide(Scene() with { Obstacles = [new("parked:1", "car", new(5, 0, 30), 1, 0, 1, 5)] });
        Assert.Null(decision.SpeedCap);
        Assert.Equal(0, decision.Brake);
        Assert.Equal(0, decision.Signal);
    }

    [Fact]
    public void NoOpposingLaneCrossingAndNoLateLaneChangeIntoAnObstacle()
    {
        var planner = new ManeuverPlanner();
        var opposing = Scene() with { Route = Road() with { LeftLane = 0 } };
        planner.Decide(opposing);
        Assert.Equal(0, planner.Decide(opposing with { Now = 1800 }).Signal);
        planner.Reset();
        var near = Scene() with { Obstacles = [Barrier(18)], Speed = 0 };
        planner.Decide(near);
        var stop = planner.Decide(near with { Now = 1800 });
        Assert.Equal(0, stop.Signal);
        Assert.True(stop.Brake >= 0.6f);
        Assert.Equal(0, stop.SpeedCap);
    }

    [Fact]
    public void SensorLossDuringManeuverBrakesAndDisablingResetsIt()
    {
        var planner = new ManeuverPlanner();
        planner.Decide(Scene()); planner.Decide(Scene(1800));
        Assert.True(planner.Decide(Scene(1900) with { TrafficFresh = false }).Brake >= 0.7f);
        planner.Decide(Scene(2000) with { Enabled = false });
        Assert.Equal(0, planner.Decide(Scene(2100)).Signal);
    }

    [Fact]
    public void AFailedLaneChangeStaysStoppedUntilTheDriverDisablesAssistance()
    {
        var planner = new ManeuverPlanner();
        planner.Decide(Scene()); planner.Decide(Scene(1800));
        Assert.True(planner.Decide(Scene(45000)).Brake >= 0.7f);
        Assert.True(planner.Decide(Scene(45100) with { Obstacles = [] }).Brake >= 0.7f);
        planner.Decide(Scene(45200) with { Enabled = false });
        Assert.Equal(0, planner.Decide(Scene(45300) with { Obstacles = [] }).Brake);
    }

    [Fact]
    public void PreviouslySeenObstructionIsNotForgottenWhenVisionIsLost()
    {
        var planner = new ManeuverPlanner();
        planner.Decide(Scene() with { Obstacles = [Barrier(15)] });
        var lost = planner.Decide(Scene(8000) with { VisionFresh = false, Obstacles = [] });
        Assert.True(lost.Brake >= 0.7f);
        Assert.Equal(0, lost.Signal);
    }

    [Fact]
    public void ReturnWaitsForTheTrailerToClearAndForANewGapCheck()
    {
        var planner = new ManeuverPlanner();
        planner.Decide(Scene()); planner.Decide(Scene(1800));
        planner.Decide(Scene(4000) with { Route = Road(2), Ego = new(-3.5f, 0, 60), Obstacles = [] });
        Assert.Equal(0, planner.Decide(Scene(15000) with { Route = Road(2), Ego = new(-3.5f, 0, 205), Obstacles = [] }).Signal);
        Assert.Equal(1, planner.Decide(Scene(16000) with { Route = Road(2), Ego = new(-3.5f, 0, 250), Obstacles = [] }).Signal);
    }

    [Fact]
    public void TollNeedsAStationaryTruckAClosedGateAndExplicitPaymentPrompt()
    {
        var planner = new ManeuverPlanner();
        var gate = Scene() with { Obstacles = [], ClosedGateId = 7, GateDistance = 8, Speed = 0 };
        Assert.False(planner.Decide(gate).PayToll);
        Assert.False(planner.Decide(gate with { Now = 2000 }).PayToll);
        Assert.True(planner.Decide(gate with { Now = 2100, TollPrompt = true }).PayToll);
        Assert.False(planner.Decide(gate with { Now = 2200, TollPrompt = true }).PayToll);
        Assert.False(planner.Decide(gate with { Now = 6200, TollPrompt = true, Speed = 3 }).PayToll);
    }

    [Fact]
    public void TollRetriesAreBoundedAndPaymentAcknowledgmentIsLatchedForThatGate()
    {
        var planner = new ManeuverPlanner();
        var gate = Scene() with { Obstacles = [], ClosedGateId = 7, GateDistance = 8, Speed = 0, TollPrompt = true };
        planner.Decide(gate);
        Assert.True(planner.Decide(gate with { Now = 2000 }).PayToll);
        Assert.True(planner.Decide(gate with { Now = 6200 }).PayToll);
        Assert.False(planner.Decide(gate with { Now = 11000 }).PayToll);
        planner.Decide(gate with { Now = 11100, PaidToll = true });
        Assert.False(planner.Decide(gate with { Now = 22000 }).PayToll);
    }

    [Theory]
    [InlineData("Press ENTER to pay the toll.", true)]
    [InlineData("Appuyez sur ENTRÉE pour payer le péage.", true)]
    [InlineData("Press ENTER to unload cargo.", false)]
    [InlineData("Press ENTER to use the ferry.", false)]
    [InlineData("Toll paid", false)]
    [InlineData("Open menu to pay", false)]
    public void PaymentTextDoesNotMatchUnrelatedGameDialogs(string text, bool expected) => Assert.Equal(expected, TollPrompt.IsPaymentRequest(text));

    [Fact]
    public void ObstacleTrackerRejectsOneFrameAndCannotCountTheSameObservationTwice()
    {
        var tracker = new ObstacleTracker();
        tracker.Update([Barrier()], 1000); tracker.Update([Barrier()], 1000);
        Assert.Empty(tracker.Confirmed(1100));
        tracker.Update([Barrier()], 1300);
        Assert.Empty(tracker.Confirmed(1400));
        tracker.Update([Barrier()], 1700);
        Assert.Single(tracker.Confirmed(1800));
        Assert.Empty(tracker.Confirmed(7000));
    }

    [Fact]
    public void InvalidGeometryAndPointsAboveTheHorizonDoNotBecomeGroundObstacles()
    {
        Assert.Null(CameraGeometry.GroundPoint(default, new(0.5f, 0.9f), 0));
        var view = Matrix4x4.CreateLookAt(new(0, 2, 0), new(0, 2, -1), Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1, 1.5f, 0.1f, 500);
        Assert.Null(CameraGeometry.GroundPoint(view * projection, new(0.5f, 0.1f), 0));
        Assert.NotNull(CameraGeometry.GroundPoint(view * projection, new(0.5f, 0.8f), 0));
    }

    [Fact]
    public void TrafficDecoderIncludesTheFortiethVehicleInTheSdkFrame()
    {
        const int size = 40 * 174;
        byte[] buffer = new byte[size];
        using var mapping = MemoryMappedFile.CreateNew(null, size);
        using var accessor = mapping.CreateViewAccessor();
        accessor.Write(39 * 174, 123f);
        accessor.Write(39 * 174 + 28, 2f);
        accessor.Write(39 * 174 + 36, 5f);
        accessor.Write(39 * 174 + 50, (short)40);
        accessor.Write(39 * 174 + 48, (short)3);
        accessor.Write(39 * 174 + 134, 77f);
        var provider = (TrafficProvider)RuntimeHelpers.GetUninitializedObject(typeof(TrafficProvider));
        void Set(string name, object value) => typeof(TrafficProvider).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(provider, value);
        Set("mmapSize", size); Set("_buffer", buffer); Set("_reader", new MemoryReader(buffer)); Set("_accessor", accessor);
        Set("_currentData", new TrafficData()); Set("_sinceReconnect", Stopwatch.StartNew()); Set("_lastVehicleById", new Dictionary<short, TrafficVehicle>());
        Set("EventString", "Tests.FortiethTrafficVehicle");
        typeof(TrafficProvider).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, null);
        var vehicles = provider.GetCurrentTrafficData()!.vehicles;
        Assert.Equal(40, vehicles.Length);
        Assert.Equal(40, vehicles[39].id);
        Assert.Equal(123, vehicles[39].Position.X);
        Assert.Equal(3, vehicles[39].trailer_count);
        Assert.Equal(77, vehicles[39].trailers[2].Position.X);
    }

    [Fact]
    public void TemporarySpeedCapCannotDiluteAnExistingBrakeRequest()
    {
        var output = (GameOutput)RuntimeHelpers.GetUninitializedObject(typeof(GameOutput));
        var values = new Dictionary<string, List<Tuple<float, float>>> { ["acceleration"] = [new(1, 1), new(1, -0.9f)] };
        typeof(GameOutput).GetField("curFrameFloats", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(output, values);
        typeof(GameOutput).GetMethod("ApplyDrivingEnvelope", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(output, [new DrivingEnvelope(1, 0.4f, 5, "test"), 8f]);
        Assert.Equal(-0.9f, ControlMixer.Resolve(values["acceleration"], true), 4);
    }

    [Fact]
    public void APerpendicularVehicleOccupiesItsFullLengthAcrossTheLane()
    {
        var vehicle = new BaseVehicle { Position = new(0, 2, 50), Size = new(2, 4, 10), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) };
        var observation = VehicleGeometry.Observe(vehicle, "parked:1", "car", 0, Road());
        Assert.True(observation.Radius >= 4.9f);
        Assert.Equal(0, observation.Position.Y, 3);
    }

    [Fact]
    public void AnOversizeTrailerExtendsTheRigClearanceBeyondTheDefaultLength()
    {
        var data = new GameTelemetryData();
        data.trailers[0].comBool.attached = true;
        data.trailers[0].comDouble.worldPosition = new(0, 0, -30);
        data.trailers[0].comVector.wheelPositions[0] = new(0, 0, -20);
        Assert.True(VehicleGeometry.RigLength(data, Vector3.Zero) >= 55);
    }

    [Fact]
    public void UnknownSpeedAndIncompleteMergeCoverageCannotAuthorizeALaneChange()
    {
        var planner = new ManeuverPlanner();
        Assert.True(planner.Decide(Scene() with { Speed = float.NaN }).Brake >= 0.7f);
        planner.Reset();
        planner.Decide(Scene());
        Assert.Equal(0, planner.Decide(Scene(1800) with { MergeCoverage = false }).Signal);
        Assert.False(VehicleGeometry.IsValid(new BaseVehicle { Position = new(float.NaN,0,0), Size = new(2,2,5) }));
    }
}
