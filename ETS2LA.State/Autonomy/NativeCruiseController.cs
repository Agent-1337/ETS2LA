using System.Numerics;

namespace ETS2LA.State.Autonomy;

public sealed record CruiseDemand(float Throttle, float Brake, float Target);

/// <summary>Deterministic longitudinal control from game-space geometry, without image inference or plugin constraints.</summary>
public sealed class NativeCruiseController
{
    private float lastThrottle;
    public void Reset() => lastThrottle = 0;
    public CruiseDemand Calculate(DrivingScene scene, float desiredSpeed, ManeuverDecision maneuver, float? signalStopDistance = null)
    {
        if (!scene.Enabled || !scene.Ready || !scene.Route.IsValid || scene.Speed < -0.1f || !float.IsFinite(scene.Speed + desiredSpeed))
        { Reset(); return new(0, 0, 0); }
        float speed = Math.Max(0, scene.Speed), target = Math.Max(0, desiredSpeed);
        if (maneuver.SpeedCap is float cap && float.IsFinite(cap) && cap >= 0) target = Math.Min(target, cap);
        float along = scene.Route.Project(scene.Ego).Along;
        foreach (var vehicle in scene.Obstacles)
        {
            if (ObstructionEvidence.IsVisual(vehicle) || !float.IsFinite(vehicle.Speed + vehicle.Radius + vehicle.Length) || !ObstructionEvidence.SameRoadSurface(vehicle, scene)) continue;
            var p = scene.Route.Project(vehicle.Position);
            if (Math.Abs(p.Lateral) >= vehicle.Radius + (scene.Rig?.HalfWidth ?? 1.3f) - 0.15f) continue;
            float gap = p.Along - along - vehicle.Length / 2 - 4;
            if (gap < -2 || gap > 250) continue;
            float reserve = 8 + speed * 1.5f;
            target = Math.Min(target, MathF.Sqrt(Math.Max(0, vehicle.Speed * vehicle.Speed + 4 * (gap - reserve))));
        }
        float horizon = Math.Clamp(speed * 5 + 30, 40, 200);
        for (float d = 0; d < horizon; d += 8)
        {
            var a = scene.Route.PointAt(along + d); var b = scene.Route.PointAt(along + d + 8); var c = scene.Route.PointAt(along + d + 16);
            var u = new Vector2(b.X - a.X, b.Z - a.Z); var v = new Vector2(c.X - b.X, c.Z - b.Z);
            if (u.Length() < 4 || v.Length() < 4) continue;
            float curvature = Math.Abs(MathF.Atan2(u.X * v.Y - u.Y * v.X, Vector2.Dot(u, v))) / ((u.Length() + v.Length()) / 2);
            if (curvature > 0.0005f) target = Math.Min(target, MathF.Sqrt(1.6f / curvature + 4 * d));
        }
        if (signalStopDistance is float stop) target = Math.Min(target, MathF.Sqrt(Math.Max(0, 4 * (stop - 5))));
        float difference = target - speed;
        float brake = Math.Max(maneuver.Brake, difference < -0.3f ? Math.Clamp(-difference * 0.2f, 0, 1) : 0);
        if (signalStopDistance is <= 6) { target = 0; brake = Math.Max(brake, 0.5f); }
        float throttle = brake > 0 || difference <= 0.1f ? 0 : Math.Clamp(difference * 0.65f, 0, 1);
        lastThrottle = brake > 0 ? 0 : Math.Min(throttle, lastThrottle + 0.2f);
        return new(lastThrottle, brake, target);
    }
}
