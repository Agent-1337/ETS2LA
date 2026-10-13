using System.Collections;
using System.Numerics;
using System.Reflection;
using ETS2LA.Game.Data;
using TruckLib;

namespace ETS2LA.State.Autonomy;

public sealed record RouteCorridor(Vector3[] Points, int Lane, int LeftLane, int RightLane, float LeftOffset, float RightOffset, bool LaneChangeInProgress)
{
    public static RouteCorridor Empty { get; } = new([], 0, 0, 0, 0, 0, false);
    public bool IsValid => Points.Length > 2;
    public (float Along, float Lateral) Project(Vector3 point)
    {
        float closest = float.PositiveInfinity, length = 0, bestAlong = 0, bestLateral = 0;
        for (int i = 1; i < Points.Length; i++)
        {
            var a = new Vector2(Points[i - 1].X, Points[i - 1].Z);
            var segment = new Vector2(Points[i].X, Points[i].Z) - a;
            float distance = segment.Length();
            if (distance < 0.01f) continue;
            var relative = new Vector2(point.X, point.Z) - a;
            float factor = Vector2.Dot(relative, segment) / (distance * distance);
            if (i > 1) factor = Math.Max(0, factor);
            if (i < Points.Length - 1) factor = Math.Min(1, factor);
            var delta = relative - segment * factor;
            if (delta.LengthSquared() < closest)
            {
                closest = delta.LengthSquared();
                bestAlong = length + factor * distance;
                bestLateral = Vector2.Dot(delta, new Vector2(segment.Y, -segment.X) / distance);
            }
            length += distance;
        }
        return (bestAlong, bestLateral);
    }

    public Vector3 Forward(Vector3 ego)
    {
        float along = Project(ego).Along, length = 0;
        for (int i = 1; i < Points.Length; i++)
        {
            var direction = Points[i] - Points[i - 1];
            float distance = direction.Length();
            if (distance > 0.01f && length + distance >= along) return Vector3.Normalize(new(direction.X, 0, direction.Z));
            length += distance;
        }
        return Vector3.UnitZ;
    }
}

/// <summary>Read-only bridge to the installed PathLib API. No proprietary assembly is needed at build time.</summary>
public static class RouteSnapshotBuilder
{
    private static T Field<T>(object instance, string name) => instance.GetType().GetField(name)?.GetValue(instance) is T value ? value : default!;
    private static object? Call(object instance, string method, Type[] signature, params object[] args) => instance.GetType().GetMethod(method, signature)?.Invoke(instance, args);

    public static RouteCorridor Build(object? data, Vector3 ego)
    {
        try
        {
            if (data == null || Field<bool>(data, "HasError") || Field<IList>(data, "PlannedPath") is not { Count: > 0 } items) return RouteCorridor.Empty;
            float total = (float)(data.GetType().GetProperty("TotalLength")?.GetValue(data) ?? 0f);
            if (!float.IsFinite(total) || total < 15 || total > 10000) return RouteCorridor.Empty;
            float factor = (float)(Call(data, "GetFactorForPoint", [typeof(Vector3)], ego) ?? 0f);
            float egoDistance = Math.Clamp(factor, 0, 1) * total;
            var points = new List<Vector3>();
            for (float distance = Math.Max(0.05f, egoDistance - 60); distance < Math.Min(total - 0.05f, egoDistance + 280); distance += 3)
                if (Call(data, "InterpolateDist", [typeof(float), typeof(float), typeof(bool)], distance, 0f, false) is OrientedPoint p && float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z)) points.Add(p.Position);
            if (points.Count < 3) return RouteCorridor.Empty;
            var current = Call(data, "GetItemForFactor", [typeof(float)], Math.Clamp(factor, 0.001f, 0.999f));
            if (current == null) return RouteCorridor.Empty;
            int lane = Field<int>(current, "laneIndex"), target = Field<int>(current, "targetLaneIndex");
            int step = Field<Direction>(current, "direction") == Direction.Forward ? 1 : -1;
            int left = lane + step, right = lane - step;
            bool Available(int candidate)
            {
                if (lane == 0 || candidate == 0 || Math.Sign(lane) != Math.Sign(candidate) || target != 0) return false;
                float length = 0;
                foreach (var item in items)
                {
                    if (item == null) return false;
                    float itemLength = (float)(Call(item, "GetLength", []) ?? 0f);
                    bool relevant = length + itemLength > egoDistance && length < egoDistance + 120;
                    length += itemLength;
                    if (!relevant) continue;
                    var road = Field<IParsedItem>(item, "item");
                    if (road is not ParsedRoad and not ParsedRoadList || !Field<bool>(item, "allowedToLaneChange")) return false;
                    var side = lane > 0 ? Side.Right : Side.Left;
                    int count = (int)(Call(road, "GetLaneCount", [typeof(Side)], side) ?? 0);
                    if (Math.Abs(candidate) > count) return false;
                }
                return length - egoDistance > 90;
            }
            float Width(int candidate)
            {
                var road = Field<IParsedItem>(current, "item");
                if (road == null) return 0;
                var side = lane > 0 ? Side.Right : Side.Left;
                var signature = new[] { typeof(float), typeof(Side), typeof(int), typeof(float) };
                var a = Call(road, "InterpolateLane", signature, 0.5f, side, Math.Abs(lane) - 1, 0f);
                var b = Call(road, "InterpolateLane", signature, 0.5f, side, Math.Abs(candidate) - 1, 0f);
                return a is OrientedPoint pa && b is OrientedPoint pb ? Vector3.Distance(pa.Position, pb.Position) : 0;
            }
            float lw = Available(left) ? Width(left) : 0, rw = Available(right) ? Width(right) : 0;
            return new(points.ToArray(), lane, lw is >= 2.5f and <= 5 ? left : 0, rw is >= 2.5f and <= 5 ? right : 0, -lw, rw, target != 0);
        }
        catch (Exception) { return RouteCorridor.Empty; }
    }
}
