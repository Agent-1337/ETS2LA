using System.Numerics;
using ETS2LA.Game.Data;

namespace ETS2LA.State.Autonomy;

public sealed record WideTurnPlan(RouteCorridor Route, SteeringTrajectoryFrame? Frame, float MaximumOffset)
{
    public bool IsActive => Frame != null;
}

/// <summary>Pre-position the tractor outside a bend and retain clearance until the articulated rig has passed.</summary>
public static class WideTurnPlanner
{
    public static WideTurnPlan Build(DrivingScene scene)
    {
        var route = scene.Route;
        if (!scene.Enabled || !scene.Ready || !route.IsValid || route.LaneChangeInProgress ||
            scene.Speed < -0.1f || scene.ClosedGateId != null || scene.Rig is not { TrailerCount: > 0 } rig ||
            !float.IsFinite(rig.TrackingSpan+rig.HalfWidth+rig.Length+rig.Articulation+scene.Speed) ||
            rig.TrackingSpan is < 4 or > 80 || rig.HalfWidth is < 0.8f or > 3)
            return new(route,null,0);

        var points=route.Points; int count=points.Length;
        if(points.Any(p=>!float.IsFinite(p.X+p.Y+p.Z))) return new(route,null,0);
        var along=new float[count]; var curvature=new float[count]; var extraRadius=new float[count];
        for(int i=1;i<count;i++) along[i]=along[i-1]+Horizontal(points[i]-points[i-1]).Length();
        for(int i=2;i<count-2;i++)
        {
            var u=Horizontal(points[i]-points[i-2]); var v=Horizontal(points[i+2]-points[i]);
            if(u.Length()<2 || v.Length()<2) continue;
            curvature[i]=MathF.Atan2(u.X*v.Y-u.Y*v.X,Vector2.Dot(u,v))/((u.Length()+v.Length())/2);
            if(Math.Abs(curvature[i])>=0.0001f)
            {
                float radius=1/Math.Abs(curvature[i]);
                extraRadius[i]=MathF.Sqrt(radius*radius+rig.TrackingSpan*rig.TrackingSpan)-radius;
            }
        }
        float egoAlong=route.Project(scene.Ego).Along, egoOffset=route.Project(scene.Ego).Lateral;
        float prepare=Math.Clamp(rig.Length*0.8f+Math.Max(0,scene.Speed)*2.5f,35,90);
        float hold=Math.Clamp(rig.Length+6,18,125), release=Math.Clamp(rig.Length*0.45f,15,35);
        var offsets=new float[count]; var lowerBounds=new float[count]; var upperBounds=new float[count];
        var obstacles=scene.Obstacles.Where(o=>!ObstructionEvidence.IsVisual(o) && ObstructionEvidence.SameRoadSurface(o,scene))
            .Select(o=>(Obstacle:o,P:route.Project(o.Position))).ToArray();

        for(int i=0;i<count;i++)
        {
            var envelope=Envelope(points[i]);
            float own=Math.Max(0,envelope.LaneWidth/2-rig.HalfWidth-0.3f);
            float desired=0;
            for(int j=2;j<count-2;j++)
            {
                float delta=along[j]-along[i], k=curvature[j];
                if(delta>prepare || delta < -hold-release || Math.Abs(k)<0.0001f) continue;
                // Extra tractor radius needed to keep the final axle on the
                // nominal curve in the steady-turn model. Rig length separately
                // determines how early to prepare and when to release the offset.
                float deficit=extraRadius[j]-own;
                float need=deficit>0.1f ? Math.Clamp(deficit+0.3f,0,2) : 0;
                float weight=delta>=0 ? Smooth(1-delta/prepare) : delta>=-hold ? 1 : Smooth(1-(-delta-hold)/release);
                float candidate=Math.Sign(k)*need*weight;
                if(Math.Abs(candidate)>Math.Abs(desired)) desired=candidate;
            }
            float Available(float extent,int side)
            {
                float available=Math.Clamp(extent-rig.HalfWidth-0.3f,0,2);
                if(!scene.TrafficFresh || !scene.MergeCoverage || !rig.HasLiveTail) return Math.Min(available,own);
                float seconds=Math.Max(0,along[i]-egoAlong)/Math.Max(3,scene.Speed);
                foreach(var (obstacle,p) in obstacles)
                {
                    if(side*p.Lateral<=0.1f) continue;
                    float predicted=p.Along+obstacle.Speed*seconds;
                    float rear=rig.Length+10+Math.Max(0,obstacle.Speed-scene.Speed)*Math.Max(4,seconds);
                    float front=Math.Max(18,scene.Speed*2)+obstacle.Length/2;
                    if(predicted<along[i]-rear-obstacle.Length/2 || predicted>along[i]+front) continue;
                    available=Math.Min(available,Math.Max(own,Math.Abs(p.Lateral)-rig.HalfWidth-obstacle.Radius-0.5f));
                }
                return available;
            }
            lowerBounds[i]=-Available(envelope.Left,-1); upperBounds[i]=Available(envelope.Right,1);
            // Enabling while already hugging the inside must not introduce a
            // multi-metre target jump that the existing controller rejects.
            if(desired!=0)
            {
                float reach=1.2f+Math.Max(0,along[i]-egoAlong)*0.12f;
                float lower=Math.Max(lowerBounds[i],egoOffset-reach), upper=Math.Min(upperBounds[i],egoOffset+reach);
                if(lower<=upper) { lowerBounds[i]=lower;upperBounds[i]=upper; }
            }
            offsets[i]=Math.Clamp(desired,lowerBounds[i],upperBounds[i]);
        }
        // Bound spatial heading changes, including at a junction's lane-width
        // transition. This is a swept arc rather than a last-moment steering kick.
        for(int pass=0;pass<2;pass++)
        {
            for(int i=1;i<count;i++) offsets[i]=Math.Clamp(offsets[i],offsets[i-1]-0.1f*(along[i]-along[i-1]),offsets[i-1]+0.1f*(along[i]-along[i-1]));
            for(int i=count-2;i>=0;i--) offsets[i]=Math.Clamp(offsets[i],offsets[i+1]-0.1f*(along[i+1]-along[i]),offsets[i+1]+0.1f*(along[i+1]-along[i]));
            for(int i=0;i<count;i++) offsets[i]=Math.Clamp(offsets[i],lowerBounds[i],upperBounds[i]);
        }
        float maximum=offsets.Select(Math.Abs).Max();
        if(maximum<0.08f) return new(route,null,0);
        var targets=new Vector3[count];
        for(int i=0;i<count;i++)
        {
            var forward=points[Math.Min(count-1,i+1)]-points[Math.Max(0,i-1)]; forward.Y=0;
            var right=forward.LengthSquared()>0.001f ? Vector3.Normalize(new(forward.Z,0,-forward.X)) : Vector3.UnitX;
            targets[i]=points[i]+right*offsets[i];
        }
        var adjusted=route with {Points=targets};
        return new(adjusted,new(points,targets,scene.Now+1000),maximum);

        RoadEnvelope Envelope(Vector3 point)
        {
            RoadEnvelope? best=null; float nearest=100;
            foreach(var envelope in route.RoadEnvelopes ?? [])
            {
                float distance=Vector3.DistanceSquared(point,envelope.Position);
                if(distance<nearest && float.IsFinite(envelope.Left+envelope.Right+envelope.LaneWidth) && envelope.LaneWidth is >= 2.5f and <= 5)
                { nearest=distance;best=envelope; }
            }
            return best ?? new(point,1.75f,1.75f,3.5f);
        }
    }

    private static Vector2 Horizontal(Vector3 value)=>new(value.X,value.Z);
    private static float Smooth(float value) { value=Math.Clamp(value,0,1);return value*value*value*(10+value*(-15+6*value)); }
}
