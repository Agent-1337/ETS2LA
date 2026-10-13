using System.Drawing;
using ETS2LA.Game.Perception;
using Xunit;

namespace ETS2LA.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RoadworkPatternTests
{
    [Fact]
    public void PlainRoadAndASingleDecorativeStripeAreNotBarricades()
    {
        using var image = new Bitmap(1280, 720);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.Gray);
        Assert.Empty(RoadworkEvidenceDetector.Detect(image));
        Rail(g, 330);
        Assert.Empty(RoadworkEvidenceDetector.Detect(image));
    }

    [Fact]
    public void ThreeAlternatingReflectiveRailsAreRecognizedAndAVehicleMaskRejectsThem()
    {
        using var image = new Bitmap(1280, 720);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.Gray);
        Rail(g, 300); Rail(g, 330); Rail(g, 360);
        Assert.Single(RoadworkEvidenceDetector.Detect(image).Where(d => d.Label.Contains("barricade")));
        Assert.Empty(RoadworkEvidenceDetector.Detect(image, [new("truck", 0.8f, new(150, 280, 600, 450))]));
    }

    private static void Rail(Graphics g, int y)
    {
        g.FillRectangle(Brushes.White, 180, y, 360, 10);
        for (int x = 180; x < 540; x += 60) g.FillRectangle(Brushes.Red, x, y, 30, 10);
    }
}
