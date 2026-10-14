using System.Numerics;
using ETS2LA.Game.Output;
using ETS2LA.Game.Perception;
using ETS2LA.State.Autonomy;
using Xunit;

namespace ETS2LA.Tests;

public sealed class PerceptionSafetyTests
{
    [Fact]
    public void MainProcessOrControlStallLatchesAnEmergencyUntilDisabled()
    {
        var policy=new ControlWatchdogPolicy();
        Assert.False(policy.Update(1000,true,true,1000,1000,false));
        Assert.False(policy.Update(1500,true,true,1000,1000,false));
        Assert.True(policy.Update(2600,true,true,1000,1000,false));
        Assert.True(policy.Latched);
        Assert.True(policy.Update(2700,true,true,2700,2700,false));
        Assert.False(policy.Update(2800,true,true,2800,2800,true)); // Pause acknowledged, never toggle it back off.
        Assert.True(policy.Latched);
        Assert.False(policy.Update(2900,false,false,2900,2900,true));
        Assert.False(policy.Latched);
    }

    [Fact]
    public void MissingPluginCommandsUseReadinessHandoverInsteadOfAnEmergencyPause()
    {
        var policy=new ControlWatchdogPolicy();
        policy.Update(1000,true,true,1000,1000,false);
        Assert.False(policy.Update(2000,true,true,2000,1300,false));
        var readiness = new DrivingReadiness();
        Assert.Equal(DrivingPhase.Active, readiness.Update(1000,true,true));
        Assert.Equal(DrivingPhase.Recovering, readiness.Update(2000,true,false));
        Assert.Equal(DrivingPhase.Handover, readiness.Update(3300,true,false));
        Assert.False(new ControlWatchdogPolicy().Update(2000,true,false,1000,1000,true));
        Assert.False(new ControlWatchdogPolicy().Update(2000,false,true,1000,1000,false));
    }

    [Fact]
    public void FirstEnableCannotPauseBeforeAnyHealthyControllerHasBeenAcquired()
    {
        var policy=new ControlWatchdogPolicy();
        Assert.False(policy.Update(1000,true,true,1000,0,false));
        Assert.False(policy.Update(1500,true,true,1500,0,false));
        Assert.False(policy.Update(2101,true,true,2101,0,false));
    }

    [Fact]
    public void NeuralJobsAreBudgetedAndNeverQueueBehindAnExistingJob()
    {
        Assert.Equal(1500,PerceptionBudget.NeuralInterval(130));
        Assert.Equal(5000,PerceptionBudget.NeuralInterval(500));
        Assert.False(PerceptionBudget.NeedsNeuralConfirmation(3000,0,130,true));
        Assert.False(PerceptionBudget.NeedsNeuralConfirmation(1400,0,130,false));
        Assert.True(PerceptionBudget.NeedsNeuralConfirmation(1600,0,130,false));
    }

    [Fact]
    public void ReusingOneNeuralResultCannotCreateMultipleIndependentObservations()
    {
        var tracker=new ObstacleTracker();
        var obstacle=new SceneObstacle("vision:cone","traffic cone",new(0,0,50),0.4f,0,0.8f,ObservedAt:1000);
        tracker.Update([obstacle],1000); tracker.Update([obstacle],1400); tracker.Update([obstacle],1800);
        Assert.Empty(tracker.Confirmed(1800));
        tracker.Update([obstacle with { ObservedAt=1900 }],1900);
        tracker.Update([obstacle with { ObservedAt=2400 }],2400);
        Assert.True(Assert.Single(tracker.Confirmed(2400)).Confirmed);
    }

    [Fact]
    public void GroundProjectionUsesRoadGradeInsteadOfAFixedHorizontalPlane()
    {
        var view=Matrix4x4.CreateLookAt(new(0,2,0),new(0,2,-1),Vector3.UnitY)*Matrix4x4.CreatePerspectiveFieldOfView(1,1.5f,0.1f,500);
        Vector2 pixel=new(0.5f,0.6f);
        var flat=CameraGeometry.GroundPoint(view,pixel,0);
        var graded=CameraGeometry.GroundPointOnRoad(view,pixel,0,Vector3.Zero,[new(0,0,0),new(0,-6,-200)]);
        Assert.NotNull(flat); Assert.NotNull(graded);
        Assert.True(graded.Value.Z<flat.Value.Z);
        Assert.InRange(Math.Abs(graded.Value.Y-graded.Value.Z*0.03f),0,0.02f);
    }

    [Fact]
    public void ConfirmedWorkZoneCanStartAnEarlyStableMergeWithoutFirstSlowingToFifty()
    {
        var road=new RouteCorridor(Enumerable.Range(-20,141).Select(i=>new Vector3(0,0,i*3)).ToArray(),1,2,0,-3.5f,0,false);
        var obstacle=new SceneObstacle("vision:1","barricade",new(0,0,235),1,0,0.8f,Confirmed:true);
        var scene=new DrivingScene(1000,true,true,Vector3.Zero,30,30,road,[obstacle],true,true,false,false,false,null,0,false,SpeedLimit:30);
        var planner=new ManeuverPlanner(); var merge=planner.Decide(scene);
        Assert.Equal(-1,merge.Signal); Assert.True(merge.SpeedCap>=30);
        Assert.True(planner.Decide(scene with { Now=1100 }).SpeedCap>=30);
    }
}
