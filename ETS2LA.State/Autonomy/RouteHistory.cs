using System.Numerics;

namespace ETS2LA.State.Autonomy;

/// <summary>Keeps the driven corridor after native Pathfinding drops completed road sections.</summary>
public sealed class RouteHistory
{
    private readonly List<Vector3> travelled = [];
    private Vector3? lastEgo;
    private static float Distance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
    public void Clear() { travelled.Clear(); lastEgo = null; }

    public RouteCorridor Include(RouteCorridor route, Vector3 ego)
    {
        if (!route.IsValid) return route;
        if (lastEgo is Vector3 last && Distance(last, ego) > 40) Clear();
        float along = route.Project(ego).Along;
        Vector3 center = route.PointAt(along);
        if (travelled.Count == 0)
            travelled.AddRange(route.Points.Where(p => route.Project(p).Along < along - 1));
        if (travelled.Count == 0 || Distance(travelled[^1], center) >= 1.5f) travelled.Add(center);
        lastEgo = ego;
        float accumulated = 0;
        for (int i = travelled.Count - 1; i > 0; i--)
        {
            accumulated += Distance(travelled[i], travelled[i - 1]);
            if (accumulated > 180) { travelled.RemoveRange(0, i); break; }
        }
        var points = travelled.Concat(route.Points.Where(p => route.Project(p).Along > along + 2)).ToArray();
        return points.Length > 2 ? route with { Points = points } : route;
    }
}
