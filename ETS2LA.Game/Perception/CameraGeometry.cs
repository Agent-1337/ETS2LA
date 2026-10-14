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

    public static SceneObstacle? Locate(ObjectDetection detection, CameraData camera, int width, int height, Vector3 ego, Vector3 forward, IReadOnlyList<Vector3>? roadSurface = null)
    {
        var box = detection.Box;
        if (box.Bottom >= height * 0.96f || box.Bottom <= height * 0.2f || box.Right <= box.Left) return null;
        var view = ViewProjection(camera);
        var pixel = new Vector2((box.Left + box.Right) / (2 * width), box.Bottom / height);
        Vector3 sector = new(camera.cx * 512, 0, camera.cy * 512);
        var point = GroundPointOnRoad(view, pixel, ego.Y, sector, roadSurface);
        if (point == null) return null;
        var world = point.Value;
        var relative = world - ego;
        float distance = new Vector2(relative.X, relative.Z).Length();
        float ground = roadSurface is { Count: > 1 } ? SurfaceHeight(roadSurface, world) : ego.Y;
        if (distance < 3 || distance > 250 || Vector3.Dot(relative, forward) < 2 || Math.Abs(world.Y-ground) > 1.5f) return null;
        var left = GroundPoint(view, new(box.Left / width, box.Bottom / height), world.Y);
        var right = GroundPoint(view, new(box.Right / width, box.Bottom / height), world.Y);
        float radius = left != null && right != null ? Vector3.Distance(left.Value, right.Value) / 2 : 0.5f;
        if (!float.IsFinite(radius) || radius > 12) return null;
        return new($"vision:{detection.Label}", detection.Label, world, Math.Clamp(radius, 0.4f, 8), 0, detection.Confidence, detection.Label is "car" ? 5 : detection.Label is "truck" ? 12 : 1);
    }

    public static Vector3? GroundPointOnRoad(Matrix4x4 view, Vector2 pixel, float groundHeight, Vector3 sector, IReadOnlyList<Vector3>? roadSurface)
    {
        var point = GroundPoint(view, pixel, groundHeight);
        if(point==null) return null;
        if(roadSurface is { Count: > 1 })
        {
            for(int iteration=0;iteration<5;iteration++)
            {
                var refined=GroundPoint(view,pixel,SurfaceHeight(roadSurface,point.Value+sector));
                if(refined==null) return null;
                point=refined;
            }
        }
        return point+sector;
    }

    public static ObjectDetection? VehicleMask(SceneObstacle vehicle, CameraData camera, int width, int height, Vector3 forward)
    {
        var view=ViewProjection(camera); var right=new Vector3(forward.Z,0,-forward.X);
        Vector3 sector=new(camera.cx*512,0,camera.cy*512);
        float left=width,top=height,rightEdge=0,bottom=0; int count=0;
        foreach(float lateral in new[]{-vehicle.Radius,vehicle.Radius})
        foreach(float along in new[]{-vehicle.Length/2,vehicle.Length/2})
        foreach(float y in new[]{0f,vehicle.Height}) {
            Vector3 world=vehicle.Position+right*lateral+forward*along+Vector3.UnitY*y;
            var clip=Vector4.Transform(new Vector4(world-sector,1),view);
            if(clip.W<=0.01f) continue;
            float x=(clip.X/clip.W+1)*width/2, pixelY=(1-clip.Y/clip.W)*height/2;
            left=Math.Min(left,x); rightEdge=Math.Max(rightEdge,x); top=Math.Min(top,pixelY); bottom=Math.Max(bottom,pixelY); count++;
        }
        return count>=4 ? new("truck",1,new(Math.Clamp(left,0,width),Math.Clamp(top,0,height),Math.Clamp(rightEdge,0,width),Math.Clamp(bottom,0,height))) : null;
    }

    private static float SurfaceHeight(IReadOnlyList<Vector3> surface, Vector3 point)
    {
        float closest=float.PositiveInfinity, height=point.Y;
        for(int i=1;i<surface.Count;i++) {
            var a=surface[i-1]; var b=surface[i];
            var segment=new Vector2(b.X-a.X,b.Z-a.Z); float length=segment.LengthSquared();
            if(length<0.01f) continue;
            float t=Math.Clamp(Vector2.Dot(new(point.X-a.X,point.Z-a.Z),segment)/length,0,1);
            float distance=Vector2.DistanceSquared(new(point.X,point.Z),new(a.X+segment.X*t,a.Z+segment.Y*t));
            if(distance<closest) { closest=distance; height=a.Y+(b.Y-a.Y)*t; }
        }
        return height;
    }
}
