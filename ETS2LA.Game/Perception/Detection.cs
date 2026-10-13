using System.Numerics;

namespace ETS2LA.Game.Perception;

public readonly record struct ImageBox(float Left, float Top, float Right, float Bottom);
public sealed record ObjectDetection(string Label, float Confidence, ImageBox Box);
public sealed record SceneObstacle(string Id, string Kind, Vector3 Position, float Radius, float Speed, float Confidence, float Length = 1);
public sealed record VisionSnapshot(long CapturedAt, IReadOnlyList<ObjectDetection> Detections,
    IReadOnlyList<SceneObstacle> Obstacles, bool HasForwardView, string Status, bool TollPrompt = false)
{
    public static VisionSnapshot Empty { get; } = new(0, [], [], false, "Visual perception starting");
}
