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
        Assert.Single(RoadworkEvidenceDetector.Detect(image, [new("truck", 0.4f, new(0, 0, 1280, 720))]));
    }

    private static void Rail(Graphics g, int y)
    {
        g.FillRectangle(Brushes.White, 180, y, 360, 10);
        for (int x = 180; x < 540; x += 60) g.FillRectangle(Brushes.Red, x, y, 30, 10);
    }

    [Fact]
    public void OrangeWhiteConeDetectionIncludesItsBaseAndRejectsPlainOrangeSigns()
    {
        using var image=new Bitmap(1280,720);
        using var graphics=Graphics.FromImage(image);
        graphics.Clear(Color.DimGray);
        graphics.FillPolygon(Brushes.OrangeRed,[new Point(600,300),new Point(588,360),new Point(612,360)]);
        graphics.FillPolygon(Brushes.White,[new Point(596,320),new Point(604,320),new Point(606,328),new Point(594,328)]);
        graphics.FillPolygon(Brushes.White,[new Point(592,340),new Point(608,340),new Point(610,348),new Point(590,348)]);
        using var workspace=new PixelWorkspace();
        var cone=Assert.Single(ConeEvidenceDetector.Detect(image,workspace));
        Assert.InRange(cone.Box.Bottom,358,362);
        graphics.Clear(Color.DimGray);
        graphics.FillPolygon(Brushes.OrangeRed,[new Point(600,300),new Point(630,330),new Point(600,360),new Point(570,330)]);
        Assert.Empty(ConeEvidenceDetector.Detect(image,workspace));
    }
}
