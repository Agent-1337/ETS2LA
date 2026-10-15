using System.Numerics;
using System.Runtime.CompilerServices;
using ETS2LA.Game.Data;
using ETS2LA.Game.Perception;
using ETS2LA.Shared;
using ETS2LA.State.Autonomy;
using TruckLib;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using Xunit;

namespace ETS2LA.Tests;

public sealed class WideTurnTests
{
    private static DrivingScene Turn(float radius=25,int direction=1,bool adjacent=true)
    {
        var points=new List<Vector3>();
        for(int i=-50;i<0;i++) points.Add(new(0,0,i*2));
        int steps=(int)Math.Ceiling(radius*MathF.PI/4);
        for(int i=0;i<=steps;i++) {float a=i*MathF.PI/2/steps;points.Add(new(direction*radius*(1-MathF.Cos(a)),0,radius*MathF.Sin(a)));}
        for(int i=1;i<=70;i++) points.Add(new(direction*(radius+i*2),0,radius));
        var bounds=points.Select(p=>new RoadEnvelope(p,adjacent && direction>0?6.75f:2.25f,adjacent && direction<0?6.75f:2.25f,4.5f)).ToArray();
        var road=new RouteCorridor(points.ToArray(),2,1,0,-4.5f,0,false,RoadEnvelopes:bounds);
        return new(Environment.TickCount64,true,true,new(0,0,-30),8,30,road,[],true,false,false,false,false,null,0,false,
            Rig:new(30,1.3f,[],0,1,true,14.4f));
    }

    private static float Offset(DrivingScene scene,WideTurnPlan plan,Vector3 at)
    {
        int index=Enumerable.Range(0,scene.Route.Points.Length).MinBy(i=>Vector3.DistanceSquared(scene.Route.Points[i],at));
        var direction=scene.Route.Points[Math.Min(index+1,scene.Route.Points.Length-1)]-scene.Route.Points[Math.Max(0,index-1)];
        var right=Vector3.Normalize(new Vector3(direction.Z,0,-direction.X));
        return Vector3.Dot(plan.Route.Points[index]-scene.Route.Points[index],right);
    }

    [Fact]
    public void RightTurnPrepositionsLeftBeforeTheBendAndKeepsAWideArc()
    {
        var scene=Turn();var plan=WideTurnPlanner.Build(scene);
        Assert.True(plan.IsActive); Assert.InRange(plan.MaximumOffset,1.8f,2.001f);
        Assert.True(Offset(scene,plan,new(0,0,-20)) < -0.8f);
        Assert.True(Offset(scene,plan,new(0,0,0)) < -1.5f);
        Assert.True(Offset(scene,plan,new(25*(1-MathF.Cos(MathF.PI/4)),0,25*MathF.Sin(MathF.PI/4))) < -1.5f);
        Assert.InRange(Offset(scene,plan,new(155,0,25)),-0.01f,0.01f);
    }

    [Fact]
    public void LeftAndRightTurnsUseMirroredOutsideTrajectories()
    {
        var right=WideTurnPlanner.Build(Turn());var left=WideTurnPlanner.Build(Turn(direction:-1));
        for(int i=0;i<right.Route.Points.Length;i++)
        {
            Assert.Equal(right.Route.Points[i].X,-left.Route.Points[i].X,3);
            Assert.Equal(right.Route.Points[i].Z,left.Route.Points[i].Z,3);
        }
    }

    [Fact]
    public void BroadBendsAndStraightRoadsDoNotCreateAnUnnecessaryWeave()
    {
        Assert.False(WideTurnPlanner.Build(Turn(500)).IsActive);
        var scene=Turn();var points=Enumerable.Range(0,150).Select(i=>new Vector3(0,0,i*2-100)).ToArray();
        Assert.False(WideTurnPlanner.Build(scene with {Route=scene.Route with {Points=points}}).IsActive);
        var shortRig=scene with {Rig=scene.Rig! with {TrackingSpan=7,Length=18}};
        Assert.True(WideTurnPlanner.Build(shortRig).MaximumOffset < WideTurnPlanner.Build(scene).MaximumOffset);
        var broad=Turn(300);
        Assert.False(WideTurnPlanner.Build(broad).IsActive);
        Assert.True(WideTurnPlanner.Build(broad with {Rig=broad.Rig! with {TrackingSpan=30,Length=60,TrailerCount=2}}).MaximumOffset>0.8f);
    }

    [Fact]
    public void KnownRoadEdgesAndUnavailableNativeCoverageLimitBorrowedSpace()
    {
        var single=Turn(adjacent:false);var own=WideTurnPlanner.Build(single);
        Assert.InRange(own.MaximumOffset,0.5f,0.651f);
        Assert.InRange(WideTurnPlanner.Build(Turn() with {TrafficFresh=false}).MaximumOffset,0.5f,0.651f);
        Assert.InRange(WideTurnPlanner.Build(Turn() with {MergeCoverage=false}).MaximumOffset,0.5f,0.651f);
        Assert.InRange(WideTurnPlanner.Build(Turn() with {Route=Turn().Route with {RoadEnvelopes=null}}).MaximumOffset,0,0.151f);
    }

    [Theory]
    [InlineData(-20,0)]
    [InlineData(-90,30)]
    public void OccupiedOrFastApproachingTrafficPreventsTheOutsideLaneSweep(float z,float speed)
    {
        var scene=Turn() with {Obstacles=[new SceneObstacle("traffic:left","car",new(-3.4f,0,z),1,speed,1,5)]};
        var plan=WideTurnPlanner.Build(scene);
        Assert.True(Offset(scene,plan,scene.Ego)>=-0.651f);
        if(speed==0) Assert.True(Offset(scene,plan,Vector3.Zero)>=-0.651f);
    }

    [Fact]
    public void ChangingLanesManualDrivingAndReverseKeepTheNativePath()
    {
        var scene=Turn();
        Assert.False(WideTurnPlanner.Build(scene with {Enabled=false}).IsActive);
        Assert.False(WideTurnPlanner.Build(scene with {Ready=false}).IsActive);
        Assert.False(WideTurnPlanner.Build(scene with {Speed=-1}).IsActive);
        Assert.False(WideTurnPlanner.Build(scene with {Route=scene.Route with {LaneChangeInProgress=true}}).IsActive);
        Assert.InRange(WideTurnPlanner.Build(scene with {Rig=scene.Rig! with {HasLiveTail=false}}).MaximumOffset,0.5f,0.651f);
        Assert.False(WideTurnPlanner.Build(scene with {Rig=scene.Rig! with {TrailerCount=0}}).IsActive);
    }

    [Fact]
    public void PlacementAndReturnAreSmoothAndWaitForTrailerClearance()
    {
        var scene=Turn();var plan=WideTurnPlanner.Build(scene);
        float last=0;
        for(int i=1;i<scene.Route.Points.Length;i++)
        {
            float offset=Offset(scene,plan,scene.Route.Points[i]);
            float previous=Offset(scene,plan,scene.Route.Points[i-1]);
            float distance=Vector3.Distance(scene.Route.Points[i],scene.Route.Points[i-1]);
            Assert.True(Math.Abs(offset-previous)<=distance*0.101f+0.005f);
            last=offset;
        }
        Assert.True(Offset(scene,plan,new(40,0,25)) < -1.5f); // Trailer is still in the bend.
        Assert.InRange(last,-0.01f,0.01f);
        var inside=scene with {Ego=new(1.4f,0,-30)};var recovery=WideTurnPlanner.Build(inside);
        Assert.True(Math.Abs(Offset(inside,recovery,new(0,0,-30))-1.4f)<=1.21f);
    }

    [Fact]
    public void SimulatedTrailerCutsLessInsideWithTheWiderTractorTrajectory()
    {
        var scene=Turn();var plan=WideTurnPlanner.Build(scene);
        float original=MaximumTrailerCut(scene.Route.Points), widened=MaximumTrailerCut(plan.Route.Points);
        Assert.True(original>2);
        Assert.True(widened<original-0.7f,$"Inside cut: original={original:F2} m, wider={widened:F2} m");

        float MaximumTrailerCut(Vector3[] path)
        {
            const float axleSpan=12;
            var axle=path[0]-Vector3.UnitZ*axleSpan;float maximum=0;
            for(int i=1;i<path.Length;i++)
            {
                int steps=(int)Math.Ceiling(Vector3.Distance(path[i-1],path[i])/0.2f);
                for(int j=1;j<=steps;j++)
                {
                    var hitch=Vector3.Lerp(path[i-1],path[i],j/(float)steps);
                    axle=hitch-Vector3.Normalize(hitch-axle)*axleSpan;
                    var p=scene.Route.Project(axle);
                    if(p.Along>=100 && p.Along<100+25*MathF.PI/2) maximum=Math.Max(maximum,p.Lateral);
                }
            }
            return maximum;
        }
    }

    [Fact]
    public void ProductionRoadAndPrefabSamplingApplyTheTargetsOnlyInsideTheSteeringPlugin()
    {
        var map=new Map();var raw=Road.Add(map,Vector3.Zero,new Vector3(0,0,120),"test");
        var road=(ParsedRoad)RuntimeHelpers.GetUninitializedObject(typeof(ParsedRoad));
        road.Road=raw;road.RightLaneOffsetsEnd=[2.25f,6.75f];road.LeftLaneOffsetsEnd=[-2.25f];
        var points=Enumerable.Range(0,61).Select(i=>road.InterpolateLane(i/60f,Side.Right,1).Position).ToArray();
        var original=road.InterpolateLane(0.5f,Side.Right,1);
        var otherLane=road.InterpolateLane(0.5f,Side.Left,0);
        SteeringTrajectory.Publish(new(points,points.Select(p=>p-new Vector3(1,0,0)).ToArray(),Environment.TickCount64+10000));
        try
        {
            Assert.Equal(original,road.InterpolateLane(0.5f,Side.Right,1));
            using(PluginExecutionContext.Enter("tumppi066.pathfinding")) Assert.Equal(original,road.InterpolateLane(0.5f,Side.Right,1));
            using(PluginExecutionContext.Enter("tumppi066.laneassist"))
            {
                Assert.Equal(original.Position-new Vector3(1,0,0),road.InterpolateLane(0.5f,Side.Right,1).Position);
                Assert.Equal(otherLane,road.InterpolateLane(0.5f,Side.Left,0)); // A different lane cannot inherit this frame.
            }
            Assert.Null(PluginExecutionContext.CurrentId);
            var curve=new NavCurve {StartPosition=Vector3.Zero,EndPosition=new(0,0,120),StartRotation=MathEx.GetNodeRotation(Vector3.UnitZ),EndRotation=MathEx.GetNodeRotation(Vector3.UnitZ),Length=120};
            var prefab=new PrefabPath(null!,null!,null!,[curve],Direction.Forward,Matrix4x4.Identity,Vector3.Zero);
            var source=Enumerable.Range(0,61).Select(i=>prefab.InterpolateDist(i*2)!.Value.Position).ToArray();
            SteeringTrajectory.Publish(new(source,source.Select(p=>p-new Vector3(1,0,0)).ToArray(),Environment.TickCount64+10000));
            var nominal=prefab.InterpolateDist(60)!.Value;
            using(PluginExecutionContext.Enter("tumppi066.laneassist")) Assert.Equal(nominal.Position-new Vector3(1,0,0),prefab.InterpolateDist(60)!.Value.Position);
        }
        finally {SteeringTrajectory.Publish(null);}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRoadEnvelopeMeasuresOnlySameDirectionLanesAndRespectsTravelOrientation(bool reverse)
    {
        var map=new Map();var raw=Road.Add(map,Vector3.Zero,new Vector3(0,0,120),"test");
        var road=(ParsedRoad)RuntimeHelpers.GetUninitializedObject(typeof(ParsedRoad));
        road.Road=raw;road.RightLaneOffsetsEnd=[2.25f,6.75f];road.LeftLaneOffsetsEnd=[-2.25f,-6.75f,-11.25f];
        var path=new SamplePath(new SampleItem(road,reverse));
        var center=road.InterpolateLane(0.5f,Side.Right,1).Position;
        var route=RouteSnapshotBuilder.Build(path,center);
        Assert.True(route.IsValid);Assert.NotEmpty(route.RoadEnvelopes!);
        foreach(var envelope in route.RoadEnvelopes!)
        {
            Assert.Equal(reverse?2.25f:6.75f,envelope.Left,2);
            Assert.Equal(reverse?6.75f:2.25f,envelope.Right,2);
            Assert.Equal(4.5f,envelope.LaneWidth,2);
        }
    }

    public sealed class SampleItem(ParsedRoad road,bool reverse)
    {
        public IParsedItem item=road;
        public int laneIndex=2,targetLaneIndex;
        public Direction direction=reverse?Direction.Backward:Direction.Forward;
        public bool allowedToLaneChange=true;
        public float GetLength()=>road.Road.Length;
        public float GetFactorFor(Vector3 point)=>road.GetFactorForPoint(point);
        public OrientedPoint Interpolate(float factor,float offset,bool affect)=>road.InterpolateLane(factor,Side.Right,1,offset);
    }
    public sealed class SamplePath(SampleItem item)
    {
        public bool HasError=false;
        public List<SampleItem> PlannedPath=[item];
        public float TotalLength=>item.GetLength();
        public float GetFactorForPoint(Vector3 point)=>item.direction==Direction.Forward?item.GetFactorFor(point):1-item.GetFactorFor(point);
        public object GetItemForFactor(float factor)=>item;
        public OrientedPoint InterpolateDist(float distance,float offset,bool affect)=>item.Interpolate(item.direction==Direction.Forward?distance/TotalLength:1-distance/TotalLength,offset,affect);
    }

    [Theory]
    [InlineData(Direction.Forward,false)]
    [InlineData(Direction.Backward,false)]
    [InlineData(Direction.Forward,true)]
    [InlineData(Direction.Backward,true)]
    public void JunctionProgressProjectsOntoTheActualCurveInsteadOfItsStraightChord(Direction direction,bool multiple)
    {
        var curve=new NavCurve {StartPosition=Vector3.Zero,EndPosition=new(25,0,25),StartRotation=MathEx.GetNodeRotation(Vector3.UnitZ),EndRotation=MathEx.GetNodeRotation(Vector3.UnitX),Length=40};
        var curves=new List<NavCurve>{curve};
        if(multiple) curves.Add(new NavCurve {StartPosition=new(25,0,25),EndPosition=new(65,0,25),StartRotation=MathEx.GetNodeRotation(Vector3.UnitX),EndRotation=MathEx.GetNodeRotation(Vector3.UnitX),Length=40});
        var prefab=new PrefabPath(null!,null!,null!,curves,direction,Matrix4x4.Identity,Vector3.Zero);
        foreach(float t in new[] {0.01f,0.1f,0.25f,0.49f,0.5f,0.51f,0.75f,0.9f,0.99f})
        {
            var position=prefab.Interpolate(t)!.Value.Position;
            Assert.InRange(prefab.GetFactorForPoint(position),t-0.001f,t+0.001f);
        }
    }
}
