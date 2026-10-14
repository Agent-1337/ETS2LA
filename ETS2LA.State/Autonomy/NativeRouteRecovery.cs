using System.Collections;
using System.Numerics;
using System.Reflection;
using ETS2LA.Game.Data;
using TruckLib;

namespace ETS2LA.State.Autonomy;

/// <summary>Re-seed the installed planner at road boundaries using its own public map factory.</summary>
public sealed class NativeRouteRecovery
{
    private long attemptedAt;
    public bool TryRecover(object plugin, Vector3 ego, Vector3 rotation, long now)
    {
        if (now - attemptedAt < 1000) return false;
        attemptedAt = now;
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = plugin.GetType();
            if (type.GetField("plannedPath", flags)?.GetValue(plugin) is not IList { Count: 0 } plan ||
                type.GetField("nearbyNodes", flags)?.GetValue(plugin) is not { } nodes) return false;
            var factory = type.Assembly.GetType("Pathfinding.Utils.PathfindingUtils")?.GetMethod("CreateFirstPlannedItem", BindingFlags.Public | BindingFlags.Static);
            if (factory == null) return false;
            float heading = rotation.X * MathF.Tau;
            var forward = new Vector3(-MathF.Sin(heading), 0, -MathF.Cos(heading));
            foreach (float advance in new[] { 2f, 5f, 10f })
            {
                var candidate = factory.Invoke(null, [nodes, ego + forward * advance, rotation]);
                if (candidate != null && IsSafeSeed(candidate, ego, forward)) { plan.Add(candidate); return true; }
            }
        }
        catch { /* Incompatible plugin versions remain under the normal handover policy. */ }
        return false;
    }

    public static bool IsSafeSeed(object candidate, Vector3 ego, Vector3 forward)
    {
        try
        {
            var type = candidate.GetType();
            if (type.GetField("item")?.GetValue(candidate) is not ParsedRoad and not ParsedRoadList) return false;
            if (type.GetMethod("GetLength")?.Invoke(candidate, null) is not float length || length < 20) return false;
            if (type.GetMethod("GetFactorFor")?.Invoke(candidate, [ego]) is not float factor || factor < -0.05f || factor > 1.02f) return false;
            var interpolate = type.GetMethod("Interpolate", [typeof(float), typeof(float), typeof(bool)]);
            float step = type.GetField("direction")?.GetValue(candidate) is Direction.Backward ? -1 : 1;
            float at = Math.Clamp(factor, 0.01f, 0.99f);
            if (interpolate?.Invoke(candidate, [at, 0f, false]) is not OrientedPoint a ||
                interpolate.Invoke(candidate, [at + step * 0.01f, 0f, false]) is not OrientedPoint b) return false;
            var delta = b.Position - a.Position;
            return Vector3.Distance(a.Position, ego) < 2.5f && delta.LengthSquared() > 0.001f &&
                Vector3.Dot(Vector3.Normalize(delta), forward) > 0.9f;
        }
        catch { return false; }
    }
}
