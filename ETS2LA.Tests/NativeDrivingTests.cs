using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ETS2LA.Game.Data;
using ETS2LA.Game.Output;
using ETS2LA.Game.Perception;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Settings;
using ETS2LA.State.Autonomy;
using TruckLib;
using Xunit;

namespace ETS2LA.Tests;

public sealed class NativeDrivingTests
{
    private static DrivingScene Scene(float speed = 12) => new(1000, true, true, Vector3.Zero, speed, 30,
        new(Enumerable.Range(-20,141).Select(i => new Vector3(0,0,i*3)).ToArray(),1,2,0,-3.5f,0,false), [], true, false,
        false,false,false,null,0,false,30);

    [Fact]
    public void StartingWithoutPluginControlsDoesNotArmOrRequestAnEmergencyPause()
    {
        var readiness = new DrivingReadiness(); var watchdog = new ControlWatchdogPolicy();
        for (int now = 1000; now < 8000; now += 100)
        {
            Assert.Equal(DrivingPhase.Preparing, readiness.Update(now,true,false));
            Assert.False(readiness.HasEngaged);
            Assert.False(watchdog.Update(now,readiness.HasEngaged,true,now,0,false));
        }
        Assert.Equal(DrivingPhase.Active, readiness.Update(8050,true,true));
        Assert.Equal(DrivingPhase.Recovering, readiness.Update(8100,true,false));
        Assert.Equal(DrivingPhase.Active, readiness.Update(8500,true,true));
    }

    [Fact]
    public void ReadinessReportsABoundedHandoverAndDoesNotSilentlyAutoResume()
    {
        var readiness = new DrivingReadiness();
        Assert.Equal(DrivingPhase.Preparing, readiness.Update(1000,true,false));
        Assert.Equal(DrivingPhase.Handover, readiness.Update(9100,true,false));
        Assert.Equal(DrivingPhase.Handover, readiness.Update(9200,true,true));
        Assert.Equal(DrivingPhase.Manual, readiness.Update(9300,false,true));
        Assert.Equal(DrivingPhase.Active, readiness.Update(9400,true,true));
    }

    [Fact]
    public void ALongGamePauseDoesNotConsumeTheControllerAcquisitionWindow()
    {
        var readiness = new DrivingReadiness();
        readiness.Update(1000,true,false);
        readiness.Update(100000,true,false,suspended:true);
        Assert.Equal(DrivingPhase.Preparing, readiness.Update(100100,true,false));
        Assert.Equal(DrivingPhase.Active, readiness.Update(100200,true,true));
    }

    [Fact]
    public void NativeCruiseAcceleratesPromptlyWithoutAnImageOrTheAccPlugin()
    {
        var cruise = new NativeCruiseController(); var scene = Scene();
        CruiseDemand demand = null!;
        for (int i=0;i<5;i++) demand=cruise.Calculate(scene,scene.SpeedLimit+5/3.6f,new("Native monitoring"));
        Assert.Equal(1,demand.Throttle); Assert.Equal(0,demand.Brake);
        Assert.Equal(scene.SpeedLimit+5/3.6f,demand.Target);
        var atTarget=cruise.Calculate(scene with { Speed=demand.Target },demand.Target,new("Native monitoring"));
        Assert.Equal(0,atTarget.Throttle); Assert.Equal(0,atTarget.Brake);
    }

    [Fact]
    public void NativeCruiseFollowsClosingTrafficAndIgnoresASeparateLane()
    {
        var lead=new SceneObstacle("traffic:1","car",new(0,0,50),1,8,1,5);
        var scene=Scene(20) with { Obstacles=[lead] };
        var demand=new NativeCruiseController().Calculate(scene,31,new("Native monitoring"));
        Assert.True(demand.Brake>0); Assert.Equal(0,demand.Throttle); Assert.True(demand.Target<20);
        var separate=new NativeCruiseController().Calculate(scene with { Obstacles=[lead with { Position=new(4,0,50) }] },31,new("Native monitoring"));
        Assert.Equal(0,separate.Brake); Assert.Equal(31,separate.Target);
        var visual=new NativeCruiseController().Calculate(scene with { Obstacles=[lead with { Id="vision:1" }] },31,new("Native monitoring"));
        Assert.Equal(0,visual.Brake); Assert.Equal(31,visual.Target);
    }

    [Fact]
    public void NativeCruisePreservesCurveAndSignalConstraints()
    {
        var scene=Scene(20);
        var stopped=new NativeCruiseController().Calculate(scene,31,new("Native monitoring"),signalStopDistance:18);
        Assert.True(stopped.Brake>0.5f); Assert.True(stopped.Target<10);
        var holding=new NativeCruiseController().Calculate(Scene(0),31,new("Native monitoring"),signalStopDistance:4);
        Assert.True(holding.Brake>=0.5f); Assert.Equal(0,holding.Throttle);
        var curve=scene.Route with {Points=Enumerable.Range(-20,141).Select(i => { float a=i*3/80f;return new Vector3(80*(1-MathF.Cos(a)),0,80*MathF.Sin(a));}).ToArray()};
        var curved=new NativeCruiseController().Calculate(scene with { Route=curve },31,new("Native monitoring"));
        Assert.True(curved.Target<15); Assert.True(curved.Brake>0);
    }

    [Fact]
    public void VerifiedParkedVehiclesCanTriggerAnEarlyMergeWithoutVisionOrAFiveSecondWait()
    {
        var scene=Scene(20) with { Obstacles=[new("parked:1","parked vehicle",new(0,0,200),1,0,1,5)] };
        var planner=new ManeuverPlanner(); Assert.Equal(0,planner.Decide(scene).Signal);
        var decision=planner.Decide(scene with { Now=1400 });
        Assert.Equal(-1,decision.Signal); Assert.Equal(0,decision.Brake);
        var occupied=scene with { Obstacles=[scene.Obstacles[0],new("traffic:rear","car",new(-3.5f,0,-80),1,30,1,5)] };
        var blocked=new ManeuverPlanner(); blocked.Decide(occupied);
        Assert.Equal(0,blocked.Decide(occupied with { Now=1400 }).Signal);
    }

    [Fact]
    public void ABlockedLeftLaneCanUseAnEmptySameDirectionRightLane()
    {
        var scene=Scene(20) with { Route=Scene().Route with {Lane=2,LeftLane=0,RightLane=1,LeftOffset=0,RightOffset=3.5f},
            Obstacles=[new("parked:1","parked vehicle",new(0,0,200),1,0,1,5)] };
        var planner=new ManeuverPlanner(); planner.Decide(scene);
        Assert.Equal(1,planner.Decide(scene with { Now=1400 }).Signal);
        var opposing=scene with { Route=scene.Route with {RightLane=0} };
        var blocked=new ManeuverPlanner(); blocked.Decide(opposing);
        Assert.Equal(0,blocked.Decide(opposing with { Now=1400 }).Signal);
    }

    [Fact]
    public void AValidatedMergeSetsTheNativeTargetInsteadOfRelyingOnlyOnAKeyPulse()
    {
        var item=new SeedCandidate(); var plan=new SeedPlan(item);
        Assert.True(RouteSnapshotBuilder.RequestLaneChange(plan,Vector3.Zero,1,2));
        Assert.Equal(2,item.targetLaneIndex);
        Assert.True(RouteSnapshotBuilder.RequestLaneChange(plan,Vector3.Zero,1,2));
        Assert.False(RouteSnapshotBuilder.RequestLaneChange(plan,Vector3.Zero,1,-1));
        Assert.False(RouteSnapshotBuilder.RequestLaneChange(plan,Vector3.Zero,1,3));
        item.targetLaneIndex=0; item.allowedToLaneChange=false;
        Assert.False(RouteSnapshotBuilder.RequestLaneChange(plan,Vector3.Zero,1,2));
        Assert.Equal(0,item.targetLaneIndex);
    }

    [Fact]
    public void RoadBoundaryRecoveryRejectsWrongDirectionAndDistantLaneSeeds()
    {
        var item=new SeedCandidate();
        Assert.True(NativeRouteRecovery.IsSafeSeed(item,Vector3.Zero,Vector3.UnitZ));
        item.Lateral=4;
        Assert.False(NativeRouteRecovery.IsSafeSeed(item,Vector3.Zero,Vector3.UnitZ));
        item.Lateral=0; item.direction=Direction.Backward;
        Assert.False(NativeRouteRecovery.IsSafeSeed(item,Vector3.Zero,Vector3.UnitZ));
    }

    public sealed class SeedPlan(SeedCandidate item)
    {
        public bool HasError=false;
        public List<SeedCandidate> PlannedPath=[item];
        public float GetFactorForPoint(Vector3 point)=>point.Z/100;
        public object GetItemForFactor(float factor)=>PlannedPath[0];
    }
    public sealed class SeedCandidate
    {
        public IParsedItem item=(ParsedRoad)RuntimeHelpers.GetUninitializedObject(typeof(ParsedRoad));
        public int laneIndex=1,targetLaneIndex;
        public bool allowedToLaneChange=true;
        public Direction direction=Direction.Forward;
        public float Lateral;
        public float GetLength()=>100;
        public float GetFactorFor(Vector3 point)=>point.Z/100;
        public OrientedPoint Interpolate(float factor,float offset,bool affect)=>new(new(Lateral,0,factor*100),Quaternion.Identity);
        public Tuple<Side,int>? GetLane(int lane)=>lane is 1 or 2 ? new(Side.Right,lane-1) : null;
    }

    [Fact]
    public void SdkWireQuaternionProducesTheCorrectWorldFootprint()
    {
        var vehicle=new BaseVehicle { Position=new(0,1,50),Size=new(2,2,6),Rotation=new(1,0,0,0) };
        var straight=VehicleGeometry.Observe(vehicle,"parked:1","car",0,Scene().Route);
        Assert.InRange(straight.Radius,0.99f,1.01f); Assert.InRange(straight.Length,5.99f,6.01f);
        var q=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2);
        vehicle.Rotation=new(q.W,q.X,q.Y,q.Z);
        var sideways=VehicleGeometry.Observe(vehicle,"parked:1","car",0,Scene().Route);
        Assert.InRange(sideways.Radius,2.99f,3.01f); Assert.InRange(sideways.Length,1.99f,2.01f);
    }

    [Fact]
    public void ReusedSdkObjectsCannotChangeAnAlreadyPublishedPlanningFrame()
    {
        var truck=new TrafficVehicle {id=1,Position=new(0,0,50),speed=10,trailer_count=1};
        truck.trailers=[new TrafficTrailer {parent=truck,Position=new(0,0,40),Size=new(2,3,12)}];
        var copy=NativeSceneSnapshot.Copy(new TrafficData {vehicles=[truck]});
        truck.Position=new(5,0,10);truck.speed=0;truck.trailers[0].Position=new(5,0,0);
        Assert.Equal(new Vector3(0,0,50),copy.vehicles[0].Position); Assert.Equal(10,copy.vehicles[0].speed);
        Assert.Equal(new Vector3(0,0,40),copy.vehicles[0].trailers[0].Position);
        Assert.Same(copy.vehicles[0],copy.vehicles[0].trailers[0].parent);
        var gate=new ETS2LA.Game.SDK.Semaphore {id=7,state=(int)GateStates.CLOSED};
        var gates=NativeSceneSnapshot.Copy(new SemaphoreData {semaphores=[gate]});
        gate.state=(int)GateStates.OPEN;
        Assert.Equal((int)GateStates.CLOSED,gates.semaphores[0].state);
    }

    [Theory]
    [InlineData(-0.5f,20,1300,true,true,true)]
    [InlineData(0.7f,20,1300,true,true,false)]
    [InlineData(-0.5f,2,1300,true,true,false)]
    [InlineData(-0.5f,20,700,true,true,false)]
    [InlineData(-0.5f,20,1300,true,false,false)]
    public void JakeBrakeIsHeldOnlyWhileEngagedAndDecelerating(float pedal,float speed,float rpm,bool engine,bool enabled,bool expected)
        => Assert.Equal(expected,EngineBrakePolicy.ShouldApply(pedal,speed,rpm,engine,enabled));

    [Fact]
    public void JakeBrakeWritesAndReleasesTheActualSdkHoldInput()
    {
        using var map=MemoryMappedFile.CreateNew(null,1024); using var buffer=map.CreateViewAccessor();
        var output=(GameOutput)RuntimeHelpers.GetUninitializedObject(typeof(GameOutput));
        Set("personalBuild",true); Set("legacyAccessor",buffer);
        int offset=0; var offsets=new Dictionary<string,int>();
        foreach(var field in typeof(ControlVariables).GetFields()) {offsets[field.Name]=offset;offset+=field.FieldType==typeof(bool?)?1:4;}
        Set("legacyShmOffsets",offsets);
        var data=new GameTelemetryData();data.truckFloat.speed=20;data.truckFloat.engineRpm=1400;data.truckBool.engineEnabled=true;
        Apply(-0.6f,true); Assert.True(buffer.ReadBoolean(offsets["motorbrake"])); Assert.False(buffer.ReadBoolean(offsets["engbraketog"]));
        Apply(1,true); Assert.False(buffer.ReadBoolean(offsets["motorbrake"]));
        Apply(-0.6f,false); Assert.False(buffer.ReadBoolean(offsets["motorbrake"]));
        void Set(string field,object value)=>typeof(GameOutput).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(output,value);
        void Apply(float pedal,bool active)=>typeof(GameOutput).GetMethod("ApplyEngineBrake",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(output,[pedal,data,active]);
    }

    [Fact]
    public void PersonalProfileDoesNotAskTheLegacySteeringPluginToPauseTheGame()
        => Assert.False(JsonNode.Parse(PersonalDrivingProfile.ApplyOverrides("AssistanceSettings.json","{}"))!["PauseWhenUnstable"]!.GetValue<bool>());
}
