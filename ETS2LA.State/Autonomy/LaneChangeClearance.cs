using ETS2LA.Game.Perception;
using System.Numerics;

namespace ETS2LA.State.Autonomy;

/// <summary>Clearance on the native cubic lane transition, including articulated off-tracking.</summary>
public static class LaneChangeClearance
{
    public static float NativeDistance(DrivingScene scene) => float.IsNaN(scene.Route.LaneChangeAvailableDistance) ? 0 :
        Math.Max(0, Math.Min(5 * Math.Max(20, scene.SpeedLimit), scene.Route.LaneChangeAvailableDistance));

    public static float RequiredDistance(DrivingScene scene, SceneObstacle obstacle, float offset)
        => RequiredDistance(scene, obstacle, offset, RoadCurvature(scene));

    private static float RequiredDistance(DrivingScene scene, SceneObstacle obstacle, float offset, float roadCurvature)
    {
        float length = NativeDistance(scene), width = Math.Abs(offset);
        if (length < 35 || width < 2.5f || !float.IsFinite(width)) return float.PositiveInfinity;
        float halfWidth = scene.Rig?.HalfWidth ?? 1.3f;
        // For a gentle transition, trailer cut-in is bounded by k * wheelbase² / 2.
        // Wheelbase squares accumulate through articulated segments. TrackingSpan
        // is their root-sum-square, measured from every attached trailer's axles.
        float span = scene.Rig?.TrackingSpan ?? MathF.Sqrt(64 + Math.Max(0, scene.RigLength - 12) * 18);
        if (!float.IsFinite(span + halfWidth) || span is < 1 or > 80 || halfWidth is < 0.8f or > 3 ||
            scene.Rig is { } rig && (!float.IsFinite(rig.Articulation) || rig.Articulation > 0.25f)) return float.PositiveInfinity;
        float curvature = 6 * width / (length * length) + roadCurvature;
        float cutIn = curvature * span * span / 2;
        float lateral = scene.Route.Project(obstacle.Position).Lateral;
        float needed = halfWidth + obstacle.Radius + 0.4f + cutIn + Math.Sign(offset) * lateral;
        if (needed >= width) return float.PositiveInfinity;
        if (needed <= 0) return 0;
        float low = 0, high = 1;
        for (int i = 0; i < 20; i++)
        {
            float t = (low + high) / 2;
            if (width * t * t * (3 - 2 * t) < needed) low = t; else high = t;
        }
        // The gap already excludes the front bumper and obstacle's near face.
        // Retain signal/control latency and ground-projection uncertainty.
        return length * high + Math.Max(0, scene.Speed) * 0.5f + 3;
    }

    public static bool CanClear(DrivingScene scene, float offset)
    {
        float egoAlong = scene.Route.Project(scene.Ego).Along;
        float curvature = RoadCurvature(scene);
        foreach (var obstacle in scene.Obstacles)
        {
            if (!ObstructionEvidence.BlocksLane(obstacle, scene)) continue;
            float gap = scene.Route.Project(obstacle.Position).Along - egoAlong - obstacle.Length / 2 - 4;
            if (gap > -3 && gap < 250 && gap < RequiredDistance(scene, obstacle, offset, curvature)) return false;
        }
        return true;
    }

    private static float RoadCurvature(DrivingScene scene)
    {
        float along = scene.Route.Project(scene.Ego).Along, maximum = 0;
        for (float distance = 0; distance < NativeDistance(scene); distance += 10)
        {
            var a = scene.Route.PointAt(along + distance);
            var b = scene.Route.PointAt(along + distance + 8);
            var c = scene.Route.PointAt(along + distance + 16);
            var u = new Vector2(b.X - a.X, b.Z - a.Z); var v = new Vector2(c.X - b.X, c.Z - b.Z);
            if (u.Length() < 4 || v.Length() < 4) continue;
            float angle = Math.Abs(MathF.Atan2(u.X * v.Y - u.Y * v.X, Vector2.Dot(u, v)));
            maximum = Math.Max(maximum, angle / ((u.Length() + v.Length()) / 2));
        }
        return maximum;
    }
}
