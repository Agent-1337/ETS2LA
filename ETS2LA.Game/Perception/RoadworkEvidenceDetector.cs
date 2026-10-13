#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ETS2LA.Game.Perception;

/// <summary>Simulator-specific evidence for reflective work-zone posts and red/white barricades.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class RoadworkEvidenceDetector
{
    private sealed record Component(int X, int Y, int Width, int Height, int Pixels)
    {
        public float CenterX => X + Width / 2f;
        public float CenterY => Y + Height / 2f;
    }

    public static IReadOnlyList<ObjectDetection> Detect(Bitmap image, IReadOnlyList<ObjectDetection>? semanticObjects = null)
    {
        int width = Math.Min(1280, image.Width), height = (int)(image.Height * (width / (float)image.Width));
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled)) graphics.DrawImage(image, new Rectangle(0, 0, width, height));
        var locked = scaled.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var pixels = new byte[locked.Stride * height];
        Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
        scaled.UnlockBits(locked);
        byte[] orange = new byte[width * height], red = new byte[width * height];
        for (int y = height / 4; y < height * 0.88f; y++)
        for (int x = 0; x < width; x++)
        {
            int p = y * locked.Stride + x * 3;
            int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
            if (r > 145 && g > 55 && r > g * 1.16f && g > b * 1.6f && b < 115) orange[y * width + x] = 1;
            if (r > 110 && r > g * 1.55f && r > b * 1.55f && g < 105 && b < 105) red[y * width + x] = 1;
        }
        var results = new List<ObjectDetection>();
        var posts = Components(orange, width, height).Where(c => c.Pixels >= 5 && c.Width < width * 0.035f && c.Height < height * 0.15f).ToList();
        var consumed = new HashSet<Component>();
        foreach (var seed in posts.OrderBy(c => c.Y))
        {
            if (consumed.Contains(seed)) continue;
            var cluster = new List<Component> { seed };
            foreach (var c in posts)
                if (c != seed && Math.Abs(c.CenterX - seed.CenterX) < Math.Max(5, seed.Width * 1.3f) && c.Y >= seed.Y && c.Y < seed.Y + height * 0.14f) cluster.Add(c);
            int x = cluster.Min(c => c.X), y = cluster.Min(c => c.Y);
            int w = cluster.Max(c => c.X + c.Width) - x, h = cluster.Max(c => c.Y + c.Height) - y;
            if (h >= 14 && h > w * 1.5f && h < height * 0.18f && cluster.Sum(c => c.Pixels) > 20)
            {
                foreach (var c in cluster) consumed.Add(c);
                float scale = image.Width / (float)width;
                results.Add(new("reflective roadwork post", 0.65f, new(x * scale, y * scale, (x + w) * scale, Math.Min(height - 1, y + h + 4) * scale)));
            }
        }
        var stripes = Components(red, width, height).Where(c => c.Pixels >= 10 && c.Width >= c.Height * 0.8f && c.Width < width * 0.16f && c.Height < height * 0.07f).ToArray();
        consumed.Clear();
        foreach (var seed in stripes)
        {
            if (consumed.Contains(seed)) continue;
            var row = stripes.Where(c => Math.Abs(c.CenterY - seed.CenterY) < Math.Max(7, seed.Height * 1.4f) && Math.Abs(c.CenterX - seed.CenterX) < width * 0.2f).OrderBy(c => c.X).ToArray();
            if (row.Length < 3) continue;
            int x = row.Min(c => c.X), y = row.Min(c => c.Y), right = row.Max(c => c.X + c.Width), bottom = row.Max(c => c.Y + c.Height);
            if (right - x < 55 || bottom - y > (right - x) * 0.5f) continue;
            int whites = 0, samples = 0;
            for (int yy = y; yy <= Math.Min(height - 1, bottom); yy++)
            for (int xx = x; xx < right; xx++)
            {
                int p = yy * locked.Stride + xx * 3;
                int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
                if (r > 130 && g > 130 && b > 130 && Math.Abs(r - g) < 35 && Math.Abs(g - b) < 35) whites++;
                samples++;
            }
            if (whites < samples * 0.08f) continue;
            foreach (var c in row) consumed.Add(c);
            float scale = image.Width / (float)width;
            results.Add(new("red and white roadwork barricade", 0.75f, new(x * scale, y * scale, right * scale, Math.Min(height - 1, bottom + Math.Max(12, (right - x) * 0.1f)) * scale)));
        }
        var filtered = results.Where(candidate => !(semanticObjects ?? []).Any(other => other.Label is "truck" or "car" or "person" &&
            (candidate.Box.Left + candidate.Box.Right) / 2 >= other.Box.Left && (candidate.Box.Left + candidate.Box.Right) / 2 <= other.Box.Right &&
            (candidate.Box.Top + candidate.Box.Bottom) / 2 >= other.Box.Top && (candidate.Box.Top + candidate.Box.Bottom) / 2 <= other.Box.Bottom)).ToArray();
        var merged = new List<ObjectDetection>();
        var bands = new List<List<float>>();
        foreach (var detection in filtered)
        {
            var overlap = merged.FindIndex(other => other.Label == detection.Label &&
                Math.Min(other.Box.Right, detection.Box.Right) > Math.Max(other.Box.Left, detection.Box.Left) &&
                Math.Abs((other.Box.Top + other.Box.Bottom - detection.Box.Top - detection.Box.Bottom) / 2) < height * (image.Width / (float)width) * 0.1f);
            if (overlap < 0) { merged.Add(detection); bands.Add([detection.Box.Top]); }
            else
            {
                if (bands[overlap].All(top => Math.Abs(top - detection.Box.Top) >= image.Height * 0.02f)) bands[overlap].Add(detection.Box.Top);
                var box = merged[overlap].Box;
                merged[overlap] = detection with { Box = new(Math.Min(box.Left, detection.Box.Left), Math.Min(box.Top, detection.Box.Top), Math.Max(box.Right, detection.Box.Right), Math.Max(box.Bottom, detection.Box.Bottom)) };
            }
        }
        return merged.Where((detection, index) => detection.Label == "reflective roadwork post" || bands[index].Count >= 3).ToArray();
    }

    private static List<Component> Components(byte[] mask, int width, int height)
    {
        var results = new List<Component>();
        var queue = new Queue<int>();
        for (int p = 0; p < mask.Length; p++)
        {
            if (mask[p] != 1) continue;
            mask[p] = 0; queue.Enqueue(p);
            int minX = width, minY = height, maxX = 0, maxY = 0, count = 0;
            while (queue.TryDequeue(out int current))
            {
                int x = current % width, y = current / width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); count++;
                foreach (var next in new[] { current - width, current + width, current - 1, current + 1 })
                    if (next >= 0 && next < mask.Length && mask[next] == 1 && Math.Abs(next % width - x) <= 1)
                    { mask[next] = 0; queue.Enqueue(next); }
            }
            results.Add(new(minX, minY, maxX - minX + 1, maxY - minY + 1, count));
        }
        return results;
    }
}
#endif
