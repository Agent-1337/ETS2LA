using System.Numerics;
using ETS2LA.Game.Perception;
using ETS2LA.Game.Telemetry;
using ETS2LA.State.Autonomy;
using Xunit;

namespace ETS2LA.Tests;

public sealed class AutonomyRefinementTests
{
    private static RouteCorridor Road(int lane = 1) => new(Enumerable.Range(-20, 141).Select(i => new Vector3(lane == 1 ? 0 : -3.5f, 0, i * 3)).ToArray(),
        lane, lane == 1 ? 2 : 0, lane == 1 ? 0 : 1, -3.5f, 3.5f, false);
    private static SceneObstacle Block(float along = 190, string id = "vision:1") => new(id, "barricade", new(0, 0, along), 0.5f, 0, 0.75f);
    private static DrivingScene Scene(long now = 1000) => new(now, true, true, Vector3.Zero, 5, 30, Road(), [], true, true, false, false, false, null, 0, false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void FocusOrPerceptionLossDoesNotBrakeAnUnobstructedRoad(bool vision, bool traffic)
    {
        var result = new ManeuverPlanner().Decide(Scene() with { VisionFresh = vision, TrafficFresh = traffic, MergeCoverage = false, Speed = 25 });
        Assert.Null(result.SpeedCap);
        Assert.Equal(0, result.Brake);
        Assert.Equal(0, result.Signal);
    }

    [Fact]
    public void AltTabDoesNotAbortAnAlreadyRequestedClearLaneChange()
    {
        var planner = new ManeuverPlanner();
        var scene = Scene() with { Obstacles = [Block()] };
        planner.Decide(scene); Assert.Equal(-1, planner.Decide(scene with { Now = 1800 }).Signal);
        var result = planner.Decide(scene with { Now = 2000, VisionFresh = false });
        Assert.Equal(0, result.Brake);
        Assert.Equal(0, result.Signal);
    }

    [Fact]
    public void SingleAndRoadsideMarkersRemainWarnings()
    {
        var lone = new SceneObstacle("vision:1", "traffic cone", new(0,0,35), 0.4f, 0, 0.8f);
        var planner = new ManeuverPlanner();
        Assert.Null(planner.Decide(Scene() with { Obstacles = [lone] }).SpeedCap);
        var roadside = Enumerable.Range(1,6).Select(i => lone with { Id = $"vision:{i}", Position = new(2.1f,0,30+i*4) }).ToArray();
        var result = planner.Decide(Scene() with { Obstacles = roadside });
        Assert.Null(result.SpeedCap); Assert.Equal(0, result.Brake);
    }

    [Fact]
    public void ThreeMarkersReallyClosingTheLaneCanTriggerPassing()
    {
        var markers = Enumerable.Range(1,3).Select(i => new SceneObstacle($"vision:{i}", "reflective roadwork post", new((i-2)*0.5f,0,190+i*2), 0.4f,0,0.7f)).ToArray();
        var planner = new ManeuverPlanner(); var scene = Scene() with { Obstacles = markers };
        planner.Decide(scene);
        Assert.Equal(-1, planner.Decide(scene with { Now = 1800 }).Signal);
    }

    [Fact]
    public void WideRoadsideConcreteBoxCannotPretendToBlockTheLane()
    {
        var result = new ManeuverPlanner().Decide(Scene() with { Obstacles = [new("vision:1", "concrete barrier", new(4,0,40), 8,0,0.9f)] });
        Assert.Equal(0,result.Brake); Assert.Null(result.SpeedCap);
        var blocked = new ManeuverPlanner().Decide(Scene() with { Obstacles = [new("vision:1", "concrete barrier", new(0,0,15), 2,0,0.8f)] });
        Assert.True(blocked.Brake >= 0.6f);
    }

    [Fact]
    public void AWeakVisualPredictionDoesNotApplyBrakes()
    {
        var result = new ManeuverPlanner().Decide(Scene() with { Obstacles = [Block(15) with { Confidence = 0.29f }] });
        Assert.Equal(0,result.Brake); Assert.Null(result.SpeedCap);
    }

    [Fact]
    public void DuplicateBoxesForOneConeCannotPretendToBeThreePhysicalMarkers()
    {
        var markers=Enumerable.Range(1,3).Select(i=>new SceneObstacle($"vision:{i}","traffic cone",new(i*0.1f,0,20),0.4f,0,0.8f)).ToArray();
        var result=new ManeuverPlanner().Decide(Scene() with { Obstacles=markers });
        Assert.Equal(0,result.Brake); Assert.Null(result.SpeedCap);
    }

    [Fact]
    public void RoadSignsAndMirrorsMaskMisclassifiedPhysicalCandidates()
    {
        var cone = new ObjectDetection("traffic cone", 0.8f, new(30,30,60,100));
        var sign = new ObjectDetection("road sign", 0.9f, new(10,10,100,140));
        Assert.Empty(ObstructionEvidence.PhysicalCandidates([cone,sign], []));
        Assert.Empty(ObstructionEvidence.PhysicalCandidates([cone,sign with { Label = "mirror" }], []));
        Assert.Single(ObstructionEvidence.PhysicalCandidates([cone], []));
        Assert.Empty(ObstructionEvidence.PhysicalCandidates([cone with { Confidence = 0.35f }], []));
    }

    [Fact]
    public void ReturnWaitsForTheLastObstacleInAConnectedWorkZone()
    {
        var planner = new ManeuverPlanner();
        var scene = Scene() with { Obstacles = [Block(), Block(240,"vision:2"), Block(290,"vision:3")] };
        planner.Decide(scene); Assert.Equal(-1,planner.Decide(scene with { Now=1800 }).Signal);
        planner.Decide(scene with { Now=4000, Route=Road(2), Ego=new(-3.5f,0,100), Obstacles=[] });
        Assert.Equal(0,planner.Decide(scene with { Now=15000, Route=Road(2), Ego=new(-3.5f,0,270), Obstacles=[] }).Signal);
        Assert.Equal(1,planner.Decide(scene with { Now=16000, Route=Road(2), Ego=new(-3.5f,0,345), Obstacles=[] }).Signal);
    }

    [Fact]
    public void LaterObservedWorkZoneExtendsTheReturnClearance()
    {
        var planner = new ManeuverPlanner(); var scene = Scene() with { Obstacles = [Block()] };
        planner.Decide(scene); planner.Decide(scene with { Now=1800 });
        planner.Decide(scene with { Now=4000, Route=Road(2), Ego=new(-3.5f,0,100), Obstacles=[Block(240,"vision:2")] });
        var later = scene with { Now=15000, Route=Road(2), Ego=new(-3.5f,0,260), Obstacles=[] };
        Assert.Equal(0,planner.Decide(later).Signal);
        Assert.Equal(1,planner.Decide(later with { Now=16000, Ego=new(-3.5f,0,300) }).Signal);
    }

    [Fact]
    public void MeasuredArticulatedTailMustClearEvenWhenTheTractorHasPassed()
    {
        var planner = new ManeuverPlanner(); var scene=Scene() with { Obstacles=[Block()], RigLength=55 };
        planner.Decide(scene); planner.Decide(scene with { Now=1800 });
        planner.Decide(scene with { Now=4000, Route=Road(2), Ego=new(-3.5f,0,100), Obstacles=[] });
        var rig=new RigGeometry(55,1.3f,[new(-3.5f,0,200),new(-3.5f,0,280)],0.25f,1);
        var near=scene with { Now=15000, Route=Road(2), Ego=new(-3.5f,0,300), Obstacles=[], Rig=rig };
        Assert.Equal(0,planner.Decide(near).Signal);
        Assert.Equal(1,planner.Decide(near with { Now=16000, Rig=rig with { Footprint=[new(-3.5f,0,235),new(-3.5f,0,280)] } }).Signal);
    }

    [Fact]
    public void RigMeasurementUsesAttachedAxlesHeadingAndTheWholeChain()
    {
        var data=new GameTelemetryData();
        data.truckPlacement.rotation=new(0.5,0,0); // Driving south, +Z.
        for(int i=0;i<2;i++) {
            var trailer=data.trailers[i]; trailer.comBool.attached=true;
            trailer.comDouble.worldPosition=new(0,0,-12-i*18);
            trailer.comDouble.worldRotation=new(0.5f,0,0);
            trailer.comUI.wheelCount=2;
            trailer.comVector.wheelPositions[0]=new(-1,0,10);
            trailer.comVector.wheelPositions[1]=new(1,0,10);
        }
        var rig=VehicleGeometry.MeasureRig(data,Vector3.Zero);
        Assert.True(rig.HasLiveTail); Assert.Equal(2,rig.TrailerCount);
        Assert.True(rig.Length >= 46); Assert.True(rig.Footprint.Min(p=>p.Z)<-45);
        data.trailers[1].comBool.attached=false;
        Assert.True(VehicleGeometry.RigLength(data,Vector3.Zero)<rig.Length);
    }

    [Fact]
    public void UnstableAndSporadicVisualLocationsCannotAccumulateConfirmation()
    {
        var tracker=new ObstacleTracker();
        tracker.Update([Block()],1000); tracker.Update([Block(192.4f)],1400); tracker.Update([Block()],1800);
        Assert.Empty(tracker.Confirmed(1900));
        tracker.Clear(); tracker.Update([Block()],1000); tracker.Update([Block()],3000); tracker.Update([Block()],5000);
        Assert.Empty(tracker.Confirmed(5100));
    }

    [Fact]
    public void TightCurveSlowsALongRigButStraightAndBroadRoadsDoNot()
    {
        RouteCorridor Curve(float radius) => new(Enumerable.Range(0,101).Select(i=>new Vector3(radius*(1-MathF.Cos(i*0.008f)),0,radius*MathF.Sin(i*0.008f))).ToArray(),1,0,0,0,0,false);
        var scene=Scene() with { Speed=20, RigLength=55, Rig=new(55,1.3f,[],0.4f,1) };
        Assert.Null(RigTurnGuard.SpeedCap(scene));
        Assert.Null(RigTurnGuard.SpeedCap(scene with { Route=Curve(500) }));
        Assert.True(RigTurnGuard.SpeedCap(scene with { Route=Curve(60) }) < 12);
        Assert.Null(RigTurnGuard.SpeedCap(scene with { Route=Curve(60), Rig=scene.Rig with { TrailerCount=0 } }));
    }

    [Fact]
    public void CurvedTrailerCorridorSurvivesNativeRemovalOfCompletedRoadSections()
    {
        var history=new RouteHistory();
        Vector3 Point(int i)=>new(100*(1-MathF.Cos(i*0.025f)),0,100*MathF.Sin(i*0.025f));
        RouteCorridor? remembered=null;
        for(int i=0;i<=60;i++) {
            var native=new RouteCorridor(Enumerable.Range(i,40).Select(Point).ToArray(),1,0,0,0,0,false);
            remembered=history.Include(native,Point(i));
        }
        Assert.NotNull(remembered);
        float truckAlong=remembered.Project(Point(60)).Along, tailAlong=remembered.Project(Point(30)).Along;
        Assert.InRange(truckAlong-tailAlong,73,77); // Seventy-five metres along the curve, not a tangent.
        var teleported=history.Include(Road(),new(0,0,300));
        Assert.DoesNotContain(Point(30),teleported.Points);
    }
}
