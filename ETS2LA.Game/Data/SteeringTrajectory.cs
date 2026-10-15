using System.Numerics;
using ETS2LA.Shared;
using TruckLib;

namespace ETS2LA.Game.Data;

/// <summary>Immutable world-space steering targets. Map extraction and route selection keep their original geometry.</summary>
public sealed class SteeringTrajectoryFrame(Vector3[] source, Vector3[] targets, long validUntil)
{
    public bool IsFresh => Environment.TickCount64 <= validUntil;

    public OrientedPoint Transform(OrientedPoint point)
    {
        if (source.Length < 3 || source.Length != targets.Length) return point;
        float best = 2.25f; int segment = -1; float factor = 0;
        for (int i = 1; i < source.Length; i++)
        {
            var delta = source[i] - source[i - 1];
            float squared = delta.X * delta.X + delta.Z * delta.Z;
            if (squared < 0.01f) continue;
            var relative = point.Position - source[i - 1];
            float t = Math.Clamp((relative.X * delta.X + relative.Z * delta.Z) / squared, 0, 1);
            float distance = Vector3.DistanceSquared(point.Position, source[i - 1] + t * delta);
            if (distance < best) { best = distance; segment = i; factor = t; }
        }
        if (segment < 0) return point;
        var from = source[segment] - source[segment - 1];
        var to = targets[segment] - targets[segment - 1];
        float yaw = MathF.IEEERemainder(MathF.Atan2(to.X, to.Z) - MathF.Atan2(from.X, from.Z), MathF.Tau);
        var offset = Vector3.Lerp(targets[segment - 1] - source[segment - 1], targets[segment] - source[segment], factor);
        return new(point.Position + offset, Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * point.Rotation));
    }
}

public static class SteeringTrajectory
{
    private static SteeringTrajectoryFrame? frame;
    public static void Publish(SteeringTrajectoryFrame? value) => Volatile.Write(ref frame, value);
    public static OrientedPoint Adjust(OrientedPoint point)
    {
        if (PluginExecutionContext.CurrentId != "tumppi066.laneassist") return point;
        var snapshot = Volatile.Read(ref frame);
        return snapshot?.IsFresh == true ? snapshot.Transform(point) : point;
    }
}
