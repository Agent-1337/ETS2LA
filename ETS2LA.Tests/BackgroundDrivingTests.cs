using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ETS2LA.Game.Output;
using ETS2LA.Game.Perception;
using ETS2LA.Game.Telemetry;
using ETS2LA.State.Autonomy;
using Xunit;

namespace ETS2LA.Tests;

public sealed class BackgroundDrivingTests
{
    [Fact]
    public void FrozenGameFramesPreserveEngagementAndAutomaticallyReacquireFreshControls()
    {
        var clock=new TelemetryFrameClock(); var readiness=new DrivingReadiness(); var watchdog=new ControlWatchdogPolicy();
        var activity=clock.Observe(1000,true,false,true,100,100,100);
        Assert.Equal(DrivingPhase.Active,readiness.Update(1000,true,activity.AcceptsControls(1000,1000,1000,1000)));
        // Keep reading a paused/suspended producer for a minute. No focus,
        // screenshot or UI heartbeat is needed and no emergency pause is armed.
        for(int now=1050;now<61000;now+=50)
        {
            activity=clock.Observe(now,true,false,true,100,100,100);
            var phase=readiness.Update(now,true,activity.AcceptsControls(now,now,now,now),activity.IsSuspended(now));
            Assert.NotEqual(DrivingPhase.Handover,phase);
            Assert.False(watchdog.Update(now,readiness.HasEngaged,activity.IsLive(now),now,1000,false));
        }
        Assert.Equal(DrivingPhase.Suspended,readiness.Phase);
        activity=clock.Observe(61000,true,false,true,200,200,200);
        Assert.False(activity.AcceptsControls(61000,60950,60950,60950));
        Assert.Equal(DrivingPhase.Recovering,readiness.Update(61000,true,false));
        Assert.True(activity.AcceptsControls(61050,61025,61025,61025));
        Assert.Equal(DrivingPhase.Active,readiness.Update(61050,true,true));
    }

    [Fact]
    public void AdvancingGameFramesWithoutASteeringControllerStillHandOver()
    {
        var clock=new TelemetryFrameClock(); var readiness=new DrivingReadiness();
        readiness.Update(1000,true,true);
        for(int now=1100;now<=2500;now+=100)
        {
            var activity=clock.Observe(now,true,false,true,(ulong)now,(ulong)now,(ulong)now);
            readiness.Update(now,true,activity.AcceptsControls(now,1000,now,now),activity.IsSuspended(now));
        }
        Assert.Equal(DrivingPhase.Handover,readiness.Phase);
        Assert.Equal(DrivingPhase.Handover,readiness.Update(2600,true,true,suspended:true));
    }

    [Fact]
    public void AConsumerHeldMappingDoesNotTurnAClosedGameIntoAnIndefiniteSuspension()
    {
        var clock=new TelemetryFrameClock();clock.Observe(1000,true,false,true,1,1,1);
        var gone=clock.Observe(5000,true,false,false,1,1,1);
        Assert.False(gone.IsSuspended(5000));Assert.False(gone.IsLive(5000));
        Assert.False(gone.AcceptsControls(5000,5000,5000,5000));
        var resumed=clock.Observe(5100,true,false,true,2,2,2);
        Assert.False(resumed.AcceptsControls(5100,5050,5050,5050));
        Assert.True(resumed.AcceptsControls(5150,5120,5120,5120));
    }

    [Theory]
    [InlineData(2UL,1UL,1UL)]
    [InlineData(1UL,2UL,1UL)]
    [InlineData(1UL,1UL,2UL)]
    public void EverySdkClockCanProveANewFrame(ulong sdk,ulong simulation,ulong render)
    {
        var clock=new TelemetryFrameClock();clock.Observe(1000,true,false,true,1,1,1);
        var activity=clock.Observe(5000,true,false,true,sdk,simulation,render);
        Assert.True(activity.IsLive(5000));Assert.Equal(5000,activity.ResumedAt);
        Assert.False(activity.IsSuspended(5500)); // Stale reader is a transport failure.
    }

    [Fact]
    public void JakeNoiseCannotChatterAndAccelerationNeverFightsTheHeldBrake()
    {
        var brake=new EngineBrakePolicy();Assert.True(brake.Update(1000,-0.5f,20,1400,true,true));
        for(int now=1050;now<5000;now+=50)
            Assert.True(brake.Update(now,now%100==0 ? -0.15f : 0.2f,20,1000,true,true));
        Assert.True(brake.Update(4999,-0.15f,20,1400,true,true));
        Assert.True(brake.Update(5000,0.8f,20,1400,true,true));
        Assert.True(brake.Update(5550,0.8f,20,1400,true,true));
        Assert.False(brake.Update(5650,0.8f,20,1400,true,true));
        Assert.False(brake.Update(5700,-0.05f,20,1000,true,true));
        Assert.True(brake.Update(5800,-0.5f,20,1400,true,true));
        Assert.False(brake.Update(5850,-0.5f,20,1400,true,false));
        Assert.True(brake.Update(5900,-0.5f,20,1400,true,true));
        Assert.False(brake.Update(5950,-0.5f,20,800,true,true));
    }

    [Fact]
    public void SmallSpeedNoiseDoesNotStartRepeatedBrakingButARealLimitReductionStillDoes()
    {
        var route=new RouteCorridor(Enumerable.Range(-20,100).Select(i=>new Vector3(0,0,i*3)).ToArray(),1,0,0,0,0,false);
        var scene=new DrivingScene(1000,true,true,Vector3.Zero,30,30,route,[],true,false,false,false,false,null,0,false);
        var cruise=new NativeCruiseController();
        for(int i=0;i<60;i++)
        {
            var demand=cruise.Calculate(scene with {Speed=30+(i%2==0 ? 0.3f : -0.15f)},30,new("Monitoring native traffic"));
            Assert.Equal(0,demand.Brake);
        }
        var reduced=cruise.Calculate(scene,20,new("Monitoring native traffic"));
        Assert.Equal(1,reduced.Brake);Assert.Equal(0,reduced.Throttle);
    }

    [Fact]
    public void PersonalPedalsUseTheDirectPluginInputAndClearBothPathsOnRelease()
    {
        using var legacyMap=MemoryMappedFile.CreateNew(null,1024);using var modernMap=MemoryMappedFile.CreateNew(null,26);
        using var legacy=legacyMap.CreateViewAccessor();using var modern=modernMap.CreateViewAccessor();
        var output=(GameOutput)RuntimeHelpers.GetUninitializedObject(typeof(GameOutput));
        Set("personalBuild",true);Set("legacyAccessor",legacy);Set("modernAccessor",modern);
        Set("legacyShmOffsets",new Dictionary<string,int>{{"aforward",122},{"abackward",126}});
        var frame=new Dictionary<string,List<Tuple<float,float>>>{{"acceleration",[new(1,1)]}};
        Set("curFrameFloats",frame);
        Write(10);Assert.Equal(1,modern.ReadSingle(13));Assert.True(modern.ReadBoolean(17));Assert.Equal(10,modern.ReadDouble(18));
        Assert.Equal(0,legacy.ReadSingle(122));Assert.Equal(0,legacy.ReadSingle(126));
        var jake=new EngineBrakePolicy();jake.Update(1000,-0.5f,20,1400,true,true);Set("engineBrake",jake);
        Write(10.5);Assert.Equal(0,modern.ReadSingle(13));Assert.True(modern.ReadBoolean(17));
        frame["acceleration"].Add(new(0.1f,-0.8f));Write(11);
        Assert.Equal(-0.8f,modern.ReadSingle(13));Assert.Equal(0.8f,legacy.ReadSingle(126));
        frame.Clear();Write(12);
        Assert.Equal(0,modern.ReadSingle(13));Assert.False(modern.ReadBoolean(17));Assert.Equal(0,legacy.ReadSingle(126));
        void Set(string field,object value)=>typeof(GameOutput).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(output,value);
        void Write(double time)=>typeof(GameOutput).GetMethod("WriteFloatFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(output,[time]);
    }

    [Fact]
    public void DisablingExperimentalBypassKeepsNativeObstacleBrakingAndItsResponseGuard()
    {
        var route=new RouteCorridor(Enumerable.Range(-20,100).Select(i=>new Vector3(0,0,i*3)).ToArray(),1,2,0,-3.5f,0,false);
        var obstacle=new SceneObstacle("parked:1","car",new(0,0,30),1,0,1,5);
        var scene=new DrivingScene(1000,true,true,Vector3.Zero,15,30,route,[obstacle],true,false,false,false,false,null,0,false);
        var planner=new ManeuverPlanner();var decision=planner.Decide(scene,automaticPassing:false);
        Assert.Equal(0,decision.Brake);Assert.Equal(0,decision.Signal);
        var demand=new NativeCruiseController().Calculate(scene,30,decision);
        Assert.True(demand.Brake>0.8f);Assert.Equal(0,demand.Throttle);Assert.InRange(demand.StoppingDistance!.Value,18,19);
        var guard=new BrakeResponseGuard();Assert.Null(guard.Update(1000,1000,true,true,demand.Brake,15,0,0,demand.StoppingDistance));
        Assert.NotNull(guard.Update(1500,1500,true,true,demand.Brake,15,0,0,demand.StoppingDistance));
        var clear=scene with {Obstacles=[]};
        var unblocked=new NativeCruiseController().Calculate(clear,30,planner.Decide(clear,automaticPassing:false));
        Assert.True(unblocked.Throttle>0);Assert.Equal(0,unblocked.Brake);Assert.Null(unblocked.StoppingDistance);
    }
}
