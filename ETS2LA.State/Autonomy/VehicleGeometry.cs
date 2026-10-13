using System.Numerics;
using ETS2LA.Game.Perception;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;

namespace ETS2LA.State.Autonomy;

public static class VehicleGeometry
{
    public static bool IsValid(BaseVehicle vehicle) => float.IsFinite(vehicle.Position.X + vehicle.Position.Y + vehicle.Position.Z) &&
        float.IsFinite(vehicle.Size.X + vehicle.Size.Y + vehicle.Size.Z) && vehicle.Size.X is >= 0.2f and <= 20 &&
        vehicle.Size.Y is >= 0.2f and <= 20 && vehicle.Size.Z is >= 0.2f and <= 80;
    public static SceneObstacle Observe(BaseVehicle vehicle, string id, string kind, float speed, RouteCorridor route)
    {
        var corners = vehicle.GetCornersOnGround();
        // SDK positions are bounding-box centers. The legacy rotation helper's
        // Y conversion is for visualization, so use the SDK height for contact level.
        Vector3 position = vehicle.Position with { Y = vehicle.Position.Y - vehicle.Size.Y / 2 };
        float radius = vehicle.Size.X / 2, length = vehicle.Size.Z;
        if (route.IsValid)
        {
            var center = route.Project(position);
            var footprint = corners.Select(route.Project).ToArray();
            radius = footprint.Max(p => Math.Abs(p.Lateral - center.Lateral));
            length = footprint.Max(p => Math.Abs(p.Along - center.Along)) * 2;
        }
        return new(id, kind, position, Math.Max(0.2f, radius), speed, 1, Math.Max(1, length));
    }

    public static float RigLength(GameTelemetryData data, Vector3 ego)
    {
        var attached = data.trailers.Where(t => t.comBool.attached).ToArray();
        float length = 12 + attached.Length * 14;
        foreach (var trailer in attached)
        {
            float distance = Vector3.Distance(ego, trailer.comDouble.worldPosition);
            if (!float.IsFinite(distance) || distance > 120) continue;
            float bodyExtent = Math.Max(14, trailer.comVector.wheelPositions.Where(p => float.IsFinite(p.Z)).Select(p => Math.Abs(p.Z)).DefaultIfEmpty(0).Max() + 5);
            length = Math.Max(length, distance + bodyExtent);
        }
        return Math.Clamp(length, 12, 120);
    }
}
