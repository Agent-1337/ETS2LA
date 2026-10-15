using System.Collections;
using System.Numerics;
using System.Reflection;
using ETS2LA.Game.Data;
using TruckLib;

namespace ETS2LA.State.Autonomy;

public sealed record RoadEnvelope(Vector3 Position, float Left, float Right, float LaneWidth);

public sealed record RouteCorridor(Vector3[] Points, int Lane, int LeftLane, int RightLane, float LeftOffset, float RightOffset, bool LaneChangeInProgress, float LaneChangeAvailableDistance = float.PositiveInfinity, RoadEnvelope[]? RoadEnvelopes = null)
{
    public static RouteCorridor Empty { get; } = new([], 0, 0, 0, 0, 0, false);
    public bool IsValid => Points.Length > 2;
    public Vector3 PointAt(float along)
    {
        float length = 0;
        for (int i = 1; i < Points.Length; i++)
        {
            var segment = Points[i] - Points[i - 1];
            float distance = new Vector2(segment.X, segment.Z).Length();
            if (distance < 0.01f) continue;
            if (length + distance >= along || i == Points.Length - 1)
                return Points[i - 1] + segment * ((along - length) / distance);
            length += distance;
        }
        return Points.FirstOrDefault();
    }
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

/// <summary>Bridge to the installed PathLib route and target-lane API. No proprietary assembly is needed at build time.</summary>
public static class RouteSnapshotBuilder
{
    private static T Field<T>(object instance, string name) => instance.GetType().GetField(name)?.GetValue(instance) is T value ? value : default!;
    private static object? Call(object instance, string method, Type[] signature, params object[] args) => instance.GetType().GetMethod(method, signature)?.Invoke(instance, args);

    public static bool RequestLaneChange(object? data, Vector3 ego, int expectedLane, int targetLane)
    {
        try
        {
            if (data == null || Field<bool>(data,"HasError") || targetLane == 0 || Math.Sign(targetLane) != Math.Sign(expectedLane) || Math.Abs(targetLane-expectedLane) != 1) return false;
            float factor = (float)(Call(data,"GetFactorForPoint",[typeof(Vector3)],ego) ?? float.NaN);
            if (!float.IsFinite(factor)) return false;
            var current = Call(data,"GetItemForFactor",[typeof(float)],Math.Clamp(factor,0.001f,0.999f));
            if (current == null || Field<int>(current,"laneIndex") != expectedLane || !Field<bool>(current,"allowedToLaneChange") ||
                Field<IParsedItem>(current,"item") is not ParsedRoad and not ParsedRoadList ||
                Field<IList>(data,"PlannedPath") is not { } path || !path.Contains(current)) return false;
            var from = Call(current,"GetLane",[typeof(int)],expectedLane) as Tuple<Side,int>;
            var to = Call(current,"GetLane",[typeof(int)],targetLane) as Tuple<Side,int>;
            if (from == null || to == null || from.Item1 != to.Item1) return false;
            var target = current.GetType().GetField("targetLaneIndex");
            int pending = target?.GetValue(current) is int value ? value : int.MinValue;
            if (pending != 0 && pending != targetLane) return false;
            // This is the installed planner's supported target-lane field. Its
            // normal tick owns curve generation, indicators and completion.
            target!.SetValue(current,targetLane);
            return true;
        }
        catch { return false; }
    }

    public static float? SignalStopDistance(object? path, DrivingScene scene, ETS2LA.Game.SDK.SemaphoreData? live)
    {
        try
        {
            if (path == null || live == null || !scene.Route.IsValid ||
                Call(path, "GetNextSemaphores", [typeof(Vector3)], scene.Ego) is not IEnumerable<ParsedSemaphore> signals) return null;
            float along = scene.Route.Project(scene.Ego).Along, nearest = float.PositiveInfinity;
            foreach (var signal in signals)
            {
                var placed = signal.GetWorldOrientedPoint().Position;
                var actual = live.semaphores.Where(s => s.type == ETS2LA.Game.SDK.SemaphoreType.TRAFFICLIGHT &&
                    s.id == signal.Semaphore.SemaphoreId && Vector3.Distance(s.GetWorldCoordinates(), placed) < 10).FirstOrDefault();
                if (actual == null) continue;
                float distance = scene.Route.Project(signal.GetCurveStartWorldCoordinates().Position).Along - along - 4;
                bool stop = actual.state is (int)ETS2LA.Game.SDK.TrafficLightState.RED or (int)ETS2LA.Game.SDK.TrafficLightState.ORANGETOGREEN;
                stop |= actual.state == (int)ETS2LA.Game.SDK.TrafficLightState.ORANGETORED && distance > scene.Speed * scene.Speed / 6 + 5;
                if (stop && distance > -2 && distance < 200) nearest = Math.Min(nearest, Math.Max(0, distance));
            }
            return float.IsFinite(nearest) ? nearest : null;
        }
        catch { return null; }
    }

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
            var envelopes = new List<RoadEnvelope>();
            var envelopeCache = new Dictionary<object,RoadEnvelope?>();
            for (float distance = Math.Max(0.05f, egoDistance - 60); distance < Math.Min(total - 0.05f, egoDistance + 280); distance += 3)
                if (Call(data, "InterpolateDist", [typeof(float), typeof(float), typeof(bool)], distance, 0f, false) is OrientedPoint p && float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z))
                {
                    points.Add(p.Position);
                    envelopes.Add(EnvelopeAt(items, distance, p.Position, envelopeCache));
                }
            if (points.Count < 3) return RouteCorridor.Empty;
            var current = Call(data, "GetItemForFactor", [typeof(float)], Math.Clamp(factor, 0.001f, 0.999f));
            if (current == null) return RouteCorridor.Empty;
            int lane = Field<int>(current, "laneIndex"), target = Field<int>(current, "targetLaneIndex");
            int step = Field<Direction>(current, "direction") == Direction.Forward ? 1 : -1;
            int left = lane + step, right = lane - step;
            float itemFactor = (float)(Call(current, "GetFactorFor", [typeof(Vector3)], ego) ?? 0f);
            float itemLength = (float)(Call(current, "GetLength", []) ?? 0f);
            float nativeAvailable = Math.Max(0, (step > 0 ? 1-itemFactor : itemFactor) * itemLength);
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
            return new(points.ToArray(), lane, lw is >= 2.5f and <= 5 ? left : 0, rw is >= 2.5f and <= 5 ? right : 0, -lw, rw, target != 0, nativeAvailable, envelopes.ToArray());
        }
        catch (Exception) { return RouteCorridor.Empty; }
    }

    private static RoadEnvelope EnvelopeAt(IList items, float distance, Vector3 position, Dictionary<object,RoadEnvelope?> cache)
    {
        float covered = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null) continue;
            float length = (float)(Call(item,"GetLength",[]) ?? 0f);
            if (distance > covered + length) { covered += length; continue; }
            if (Field<IParsedItem>(item,"item") is ParsedRoad or ParsedRoadList)
                return Cached(item,position) is { } bounds ? bounds with {Position=position} : new(position,1.75f,1.75f,3.5f);
            // Junctions inherit the entry/exit road's available lane space. Do
            // not infer asphalt or opposing-lane clearance from the turn radius.
            object? before = i > 0 ? items[i-1] : Field<object>(item,"lastItem");
            object? after = i+1 < items.Count ? items[i+1] : null;
            var entry = before == null ? null : Cached(before,null,end:true);
            var exit = after == null ? null : Cached(after,null,end:false);
            if (entry == null || exit == null) return new(position,1.75f,1.75f,3.5f);
            return new(position,Math.Min(entry.Left,exit.Left),Math.Min(entry.Right,exit.Right),Math.Min(entry.LaneWidth,exit.LaneWidth));
        }
        return new(position,1.75f,1.75f,3.5f);

        RoadEnvelope? Cached(object item,Vector3? at,bool end=false)
        {
            if (!cache.TryGetValue(item,out var result)) { result=RoadEnvelopeFor(item,at,end);cache[item]=result; }
            return result;
        }
    }

    private static RoadEnvelope? RoadEnvelopeFor(object item, Vector3? position, bool end = false)
    {
        try
        {
            var road = Field<IParsedItem>(item,"item");
            if (road is not ParsedRoad and not ParsedRoadList || Field<int>(item,"targetLaneIndex") != 0) return null;
            int lane = Field<int>(item,"laneIndex");
            if (lane == 0) return null;
            var side = lane > 0 ? Side.Right : Side.Left;
            int index = Math.Abs(lane)-1, count = (int)(Call(road,"GetLaneCount",[typeof(Side)],side) ?? 0);
            if (count < 1 || count > 12 || index >= count) return null;
            int step = Field<Direction>(item,"direction") == Direction.Forward ? 1 : -1;
            float t = position is Vector3 at ? (float)(Call(item,"GetFactorFor",[typeof(Vector3)],at) ?? 0.5f) : end == (step > 0) ? 0.99f : 0.01f;
            t = Math.Clamp(t,0.01f,0.99f);
            var signature = new[] {typeof(float),typeof(Side),typeof(int),typeof(float)};
            if (Call(road,"InterpolateLane",signature,t,side,index,0f) is not OrientedPoint center ||
                Call(road,"InterpolateLane",signature,t+step*0.005f,side,index,0f) is not OrientedPoint ahead) return null;
            var forward = ahead.Position-center.Position; forward.Y=0;
            if (forward.LengthSquared()<0.0001f) return null;
            forward=Vector3.Normalize(forward); var right=new Vector3(forward.Z,0,-forward.X);
            float leftExtent=0,rightExtent=0,width=4.5f;
            foreach (int candidate in Enumerable.Range(0,count))
                if (Call(road,"InterpolateLane",signature,t,side,candidate,0f) is OrientedPoint other)
                {
                    float lateral=Vector3.Dot(other.Position-center.Position,right);
                    leftExtent=Math.Max(leftExtent,-lateral); rightExtent=Math.Max(rightExtent,lateral);
                    if(candidate!=index && Math.Abs(lateral) is >= 2.5f and <= 5) width=Math.Min(width,Math.Abs(lateral));
                }
            return new(center.Position,leftExtent+width/2,rightExtent+width/2,width);
        }
        catch { return null; }
    }
}
