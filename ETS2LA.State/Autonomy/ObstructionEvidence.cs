using ETS2LA.Game.Perception;

namespace ETS2LA.State.Autonomy;

/// <summary>Observed work-zone markers are not automatically a blocked driving lane.</summary>
public static class ObstructionEvidence
{
    public static bool IsVisual(SceneObstacle obstacle) => obstacle.Id.StartsWith("vision:", StringComparison.Ordinal);
    public static bool IsMarker(SceneObstacle obstacle) => obstacle.Kind is "traffic cone" or "reflective roadwork post";
    public static bool SameRoadSurface(SceneObstacle obstacle, DrivingScene scene) =>
        Math.Abs(obstacle.Position.Y - scene.Route.PointAt(scene.Route.Project(obstacle.Position).Along).Y) < 3;

    public static bool BlocksLane(SceneObstacle obstacle, DrivingScene scene, float offset = 0)
    {
        if (!float.IsFinite(obstacle.Position.X + obstacle.Position.Y + obstacle.Position.Z + obstacle.Radius + obstacle.Length) ||
            !SameRoadSurface(obstacle,scene) || obstacle.Speed >= 1.5f) return false;
        float lateral = scene.Route.Project(obstacle.Position).Lateral - offset;
        // Require actual overlap with the body, not the former wide tolerance
        // that classified adjacent parked trucks and roadside barriers as roadblocks.
        float halfWidth = scene.Rig?.HalfWidth ?? 1.3f;
        float overlap = halfWidth + obstacle.Radius - Math.Abs(lateral);
        if (overlap < (IsVisual(obstacle) ? 0.55f : 0.15f)) return false;
        if (!IsVisual(obstacle)) return true;
        if (obstacle.Confidence < 0.5f) return false;
        // A long concrete wall's image box can exaggerate its ground width.
        // Its contact center must actually occupy the driving corridor; a
        // roadside retaining wall cannot trigger braking through box width alone.
        if (obstacle.Kind == "concrete barrier" && Math.Abs(lateral) > halfWidth - 0.3f) return false;
        if (!IsMarker(obstacle)) return true;
        // A single roadside cone or reflective post is a warning. A cluster of
        // independent markers intruding into this corridor is a physical closure.
        float along = scene.Route.Project(obstacle.Position).Along;
        var distinct = new List<SceneObstacle> { obstacle };
        foreach (var other in scene.Obstacles)
            if (other.Id != obstacle.Id && IsVisual(other) && IsMarker(other) && other.Confidence >= 0.5f &&
                SameRoadSurface(other,scene) &&
                Math.Abs(scene.Route.Project(other.Position).Along - along) < 18 &&
                halfWidth + other.Radius - Math.Abs(scene.Route.Project(other.Position).Lateral - offset) >= 0.55f &&
                distinct.All(seen => System.Numerics.Vector3.Distance(seen.Position, other.Position) >= 1.25f)) distinct.Add(other);
        return distinct.Count >= 3;
    }

    public static IReadOnlyList<ObjectDetection> PhysicalCandidates(IReadOnlyList<ObjectDetection> semantic, IReadOnlyList<ObjectDetection> patterned)
    {
        var candidates = semantic.Where(d => d.Label == "concrete barrier" && d.Confidence >= 0.52f ||
                                             d.Label == "traffic cone" && d.Confidence >= 0.5f ||
                                             d.Label is "car" or "truck" && d.Confidence >= 0.65f).Concat(patterned);
        return candidates.Where(d => !semantic.Any(other => other.Label is "road sign" or "mirror" && Contains(other.Box, d.Box))).ToArray();
    }
    private static bool Contains(ImageBox outer, ImageBox inner) => (inner.Left + inner.Right) / 2 >= outer.Left &&
        (inner.Left + inner.Right) / 2 <= outer.Right && (inner.Top + inner.Bottom) / 2 >= outer.Top && (inner.Top + inner.Bottom) / 2 <= outer.Bottom;
}
