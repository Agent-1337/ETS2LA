using System.Numerics;
using ETS2LA.Game.SDK;
using ETS2LA.Shared;
using TruckLib;

namespace ETS2LA.Game.Perception;

public static class CameraGeometry
{
    public static Matrix4x4 ViewProjection(CameraData camera)
    {
        // Match the coordinate conversion used by the existing AR renderer.
        var euler = Quaternion.Conjugate(camera.rotation).ToEuler();
        var rotation = Quaternion.CreateFromYawPitchRoll(-euler.Y + MathF.PI, -euler.Z + MathF.PI, -euler.X);
        var world = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(camera.position);
        return Matrix4x4.Invert(world, out var view) ? view * Matrix4x4.Transpose(camera.projection) : default;
    }

    public static Vector3? GroundPoint(Matrix4x4 viewProjection, Vector2 imagePoint, float groundHeight)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return null;
        Vector4 near = Vector4.Transform(new Vector4(imagePoint.X * 2 - 1, 1 - imagePoint.Y * 2, 0, 1), inverse);
        Vector4 far = Vector4.Transform(new Vector4(imagePoint.X * 2 - 1, 1 - imagePoint.Y * 2, 1, 1), inverse);
        if (Math.Abs(near.W) < 0.000001f || Math.Abs(far.W) < 0.000001f) return null;
        var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var direction = new Vector3(far.X, far.Y, far.Z) / far.W - origin;
        if (Math.Abs(direction.Y) < 0.000001f) return null;
        float distance = (groundHeight - origin.Y) / direction.Y;
        if (distance <= 0) return null;
        var point = origin + direction * distance;
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z) ? point : null;
    }

    public static SceneObstacle? Locate(ObjectDetection detection, CameraData camera, int width, int height, Vector3 ego, Vector3 forward)
    {
        var box = detection.Box;
        if (box.Bottom >= height * 0.96f || box.Bottom <= height * 0.2f || box.Right <= box.Left) return null;
        var view = ViewProjection(camera);
        var point = GroundPoint(view, new((box.Left + box.Right) / (2 * width), box.Bottom / height), ego.Y);
        if (point == null) return null;
        Vector3 sector = new(camera.cx * 512, 0, camera.cy * 512);
        var world = point.Value + sector;
        var relative = world - ego;
        float distance = new Vector2(relative.X, relative.Z).Length();
        if (distance < 3 || distance > 250 || Vector3.Dot(relative, forward) < 2 || Math.Abs(relative.Y) > 3) return null;
        var left = GroundPoint(view, new(box.Left / width, box.Bottom / height), ego.Y);
        var right = GroundPoint(view, new(box.Right / width, box.Bottom / height), ego.Y);
        float radius = left != null && right != null ? Vector3.Distance(left.Value, right.Value) / 2 : 0.5f;
        if (!float.IsFinite(radius) || radius > 12) return null;
        return new($"vision:{detection.Label}", detection.Label, world, Math.Clamp(radius, 0.4f, 8), 0, detection.Confidence);
    }
}
