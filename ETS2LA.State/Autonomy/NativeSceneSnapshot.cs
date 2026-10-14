using ETS2LA.Game.SDK;

namespace ETS2LA.State.Autonomy;

/// <summary>SDK providers reuse mutable objects. Copy on their publishing thread so planning sees one complete frame.</summary>
public static class NativeSceneSnapshot
{
    public static TrafficData Copy(TrafficData data) => new() { vehicles = data.vehicles.Where(v => v.id != 0).Select(CopyVehicle).ToArray() };
    public static ParkedVehicleData Copy(ParkedVehicleData data) => new() { vehicles = data.vehicles.Where(v => v.id != 0).Select(v => new ParkedVehicle {
        id = v.id, Position = v.Position, Rotation = v.Rotation, Size = v.Size, isTrailer = v.isTrailer
    }).ToList() };
    public static SemaphoreData Copy(SemaphoreData data) => new() { semaphores = data.semaphores.Select(s => new ETS2LA.Game.SDK.Semaphore {
        id = s.id, type = s.type, position = s.position, rotation = s.rotation, cx = s.cx, cy = s.cy, state = s.state, time_remaining = s.time_remaining
    }).ToArray() };

    private static TrafficVehicle CopyVehicle(TrafficVehicle original)
    {
        var copy = new TrafficVehicle { id = original.id, Position = original.Position, Rotation = original.Rotation, Size = original.Size,
            speed = original.speed, acceleration = original.acceleration, trailer_count = original.trailer_count, isTMP = original.isTMP, isTrailer = original.isTrailer };
        copy.trailers = original.trailers.Take(Math.Clamp(original.trailer_count, (short)0, (short)3)).Select(t => new TrafficTrailer {
            parent = copy, Position = t.Position, Size = t.Size, Rotation = t.Rotation
        }).ToArray();
        return copy;
    }
}
