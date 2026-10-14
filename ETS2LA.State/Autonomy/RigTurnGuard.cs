using System.Numerics;

namespace ETS2LA.State.Autonomy;

/// <summary>Anticipates tight turns for a long/articulated rig, without replacing the native steering controller.</summary>
public static class RigTurnGuard
{
    public static float? SpeedCap(DrivingScene scene)
    {
        if (scene.Rig is not { TrailerCount: > 0 } rig || rig.Length < 22 || !scene.Route.IsValid) return null;
        float along = scene.Route.Project(scene.Ego).Along;
        float horizon = Math.Clamp(scene.Speed * 4 + rig.Length / 2, 35, 100);
        float cap = float.PositiveInfinity;
        for (float distance = 0; distance < horizon; distance += 5)
        {
            Vector3 a = scene.Route.PointAt(along + distance), b = scene.Route.PointAt(along + distance + 8), c = scene.Route.PointAt(along + distance + 16);
            var u = new Vector2(b.X - a.X, b.Z - a.Z); var v = new Vector2(c.X - b.X, c.Z - b.Z);
            if (u.Length() < 4 || v.Length() < 4) continue;
            float angle = Math.Abs(MathF.Atan2(u.X * v.Y - u.Y * v.X, Vector2.Dot(u, v)));
            float curvature = angle / ((u.Length() + v.Length()) / 2);
            // Broad highway bends retain the normal cruise profile.
            if (curvature < 1f / 180) continue;
            float lengthFactor = 1 + Math.Clamp((rig.Length - 26) * 0.012f, 0, 0.7f);
            float articulationFactor = 1 + Math.Clamp(rig.Articulation - 0.15f, 0, 0.6f);
            float turnSpeed = Math.Max(2.8f, MathF.Sqrt(1.6f / curvature) / (lengthFactor * articulationFactor));
            cap = Math.Min(cap, MathF.Sqrt(turnSpeed * turnSpeed + 3 * distance));
        }
        return float.IsFinite(cap) ? cap : null;
    }
}
