using System.Numerics;
using ETS2LA.Game.Perception;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;

namespace ETS2LA.State.Autonomy;

public sealed record RigGeometry(float Length, float HalfWidth, IReadOnlyList<Vector3> Footprint, float Articulation, int TrailerCount, bool HasLiveTail = true, float TrackingSpan = 24);

public static class VehicleGeometry
{
    public static bool IsValid(BaseVehicle vehicle) => float.IsFinite(vehicle.Position.X + vehicle.Position.Y + vehicle.Position.Z) &&
        float.IsFinite(vehicle.Size.X + vehicle.Size.Y + vehicle.Size.Z) && vehicle.Size.X is >= 0.2f and <= 20 &&
        vehicle.Size.Y is >= 0.2f and <= 20 && vehicle.Size.Z is >= 0.2f and <= 80;
    public static SceneObstacle Observe(BaseVehicle vehicle, string id, string kind, float speed, RouteCorridor route)
    {
        var corners = GroundCorners(vehicle);
        // SDK positions are bounding-box centers. The legacy rotation helper's
        // Y conversion is for visualization, so use the SDK height for contact level.
        Vector3 position = vehicle.Position with { Y = vehicle.Position.Y - vehicle.Size.Y / 2 };
        float radius = vehicle.Size.X / 2, length = vehicle.Size.Z;
        if (route.IsValid && corners.Length == 4)
        {
            var center = route.Project(position);
            var footprint = corners.Select(route.Project).ToArray();
            radius = footprint.Max(p => Math.Abs(p.Lateral - center.Lateral));
            length = footprint.Max(p => Math.Abs(p.Along - center.Along)) * 2;
        }
        return new(id, kind, position, Math.Max(0.2f, radius), speed, 1, Math.Max(1, length), Height: vehicle.Size.Y);
    }

    public static Vector3[] GroundCorners(BaseVehicle vehicle)
    {
        // The SDK wire layout is W,X,Y,Z; the existing public Rotation property
        // retains that layout for visualization plugin compatibility. Transform
        // body geometry with System.Numerics' actual X,Y,Z,W ordering.
        var raw = vehicle.Rotation;
        var rotation = new Quaternion(raw.Y, raw.Z, raw.W, raw.X);
        if (!float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 0.5f) return [];
        rotation = Quaternion.Normalize(rotation);
        var half = vehicle.Size / 2;
        return new[] { new Vector3(-half.X, -half.Y, -half.Z), new(half.X, -half.Y, -half.Z),
            new Vector3(half.X, -half.Y, half.Z), new(-half.X, -half.Y, half.Z) }
            .Select(p => vehicle.Position + Vector3.Transform(p, rotation)).ToArray();
    }

    public static float RigLength(GameTelemetryData data, Vector3 ego) => MeasureRig(data, ego).Length;

    public static RigGeometry MeasureRig(GameTelemetryData data, Vector3 ego)
    {
        var attached = data.trailers.Where(t => t.comBool.attached).ToArray();
        float length = 12 + attached.Length * 18;
        float halfWidth = 1.3f, articulation = 0;
        int measuredTrailers = 0;
        var footprint = new List<Vector3>();
        float truckHeading = (float)data.truckPlacement.rotation.X * MathF.Tau;
        var truckWheels = data.configVector.truckWheelPositions.Take(Math.Clamp(data.configUI.truckWheelCount, 0, 16)).Where(Finite).ToArray();
        float tractorSpan = truckWheels.Length > 1 ? Math.Clamp(truckWheels.Max(p => p.Z) - truckWheels.Min(p => p.Z), 4, 12) : 8;
        float trackingSquared = tractorSpan * tractorSpan;
        float tractorRear = Math.Max(8, Math.Max(data.configVector.truckHookPosition.Z + 3, truckWheels.Select(p => p.Z + 3).DefaultIfEmpty(8).Max()));
        AddBody(ego, truckHeading, -4, tractorRear, 1.3f);
        float previousHeading = truckHeading;
        foreach (var trailer in attached)
        {
            var position = trailer.comDouble.worldPosition;
            float distance = Vector3.Distance(ego, position), heading = trailer.comDouble.worldRotation.X * MathF.Tau;
            if (!Finite(position) || !float.IsFinite(heading) || distance > 120 || position == Vector3.Zero && ego.Length() > 120) continue;
            // Local SDK axes: X right, Z backward. Heading is measured in turns.
            // The last axle plus a conservative six-metre overhang bounds the tail.
            var wheels = trailer.comVector.wheelPositions.Take(Math.Clamp(trailer.comUI.wheelCount, 0, 16)).Where(Finite).Where(p => p != Vector3.Zero).ToArray();
            // Retain configured positions on SDK revisions without wheelCount.
            if (wheels.Length == 0) wheels = trailer.comVector.wheelPositions.Where(Finite).Where(p => p != Vector3.Zero).ToArray();
            float rear = wheels.Length > 0 ? Math.Max(8, wheels.Max(p => p.Z) + 6) : 18;
            float front = Math.Min(-3, Math.Min(trailer.comVector.hookPosition.Z - 1, wheels.Select(p => p.Z - 6).DefaultIfEmpty(-3).Min()));
            float width = Math.Clamp(wheels.Select(p => Math.Abs(p.X) + 0.55f).DefaultIfEmpty(1.3f).Max(), 1.3f, 2.5f);
            halfWidth = Math.Max(halfWidth, width);
            AddBody(position, heading, front, rear, width);
            if (wheels.Length > 0 && distance > 2) measuredTrailers++;
            float trailerSpan = wheels.Length > 0 ?
                Math.Clamp(Math.Abs(wheels.Average(p => p.Z) - trailer.comVector.hookPosition.Z), 4, 24) : 24;
            trackingSquared += trailerSpan * trailerSpan;
            length = Math.Max(length, distance + Math.Max(Math.Abs(front), rear));
            articulation = Math.Max(articulation, Math.Abs(MathF.IEEERemainder(heading - previousHeading, MathF.Tau)));
            previousHeading = heading;
        }
        return new(Math.Clamp(length, 12, 120), halfWidth, footprint.ToArray(), articulation, attached.Length, measuredTrailers == attached.Length, MathF.Sqrt(trackingSquared));

        void AddBody(Vector3 position, float heading, float front, float rear, float width)
        {
            if (!float.IsFinite(heading + front + rear + width)) return;
            var rotation = Matrix4x4.CreateRotationY(heading);
            foreach (float x in new[] { -width, width })
            foreach (float z in new[] { front, rear })
                footprint.Add(position + Vector3.Transform(new(x, 0, z), rotation));
        }
    }
    private static bool Finite(Vector3 point) => float.IsFinite(point.X + point.Y + point.Z);
}
