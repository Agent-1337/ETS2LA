#if WINDOWS
using System.Drawing;

namespace ETS2LA.Game.Perception;

/// <summary>Fast orange/white cone evidence in the forward road view, including small distant cones.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class ConeEvidenceDetector
{
    public static IReadOnlyList<ObjectDetection> Detect(Bitmap image, PixelWorkspace? workspace=null)
    {
        using var owned=workspace==null?new PixelWorkspace():null;
        var buffer=workspace??owned!;
        // Preserve detail on the road instead of shrinking the cabin, sky and
        // mirrors into the same 800-pixel neural-network input.
        var region=new Rectangle((int)(image.Width*0.22f),(int)(image.Height*0.28f),(int)(image.Width*0.6f),(int)(image.Height*0.38f));
        buffer.Read(image,1536,region);
        var mask=buffer.Orange; var pixels=buffer.Pixels; int width=buffer.Width,height=buffer.Height,stride=buffer.Stride;
        for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
            int p=y*stride+x*3; int b=pixels[p],g=pixels[p+1],r=pixels[p+2];
            if(r>135 && g>45 && r>g*1.25f && g>b*1.5f && b<120) mask[y*width+x]=1;
        }
        var parts=RoadworkEvidenceDetector.Components(mask,width,height).Where(c=>c.Pixels>=2 && c.Width<width*0.045f && c.Height<height*0.3f).OrderBy(c=>c.Y).ToArray();
        var detections=new List<ObjectDetection>();
        foreach(var top in parts) {
            var lower=parts.Where(c=>c.Y>=top.Y+top.Height && c.Y-top.Y<Math.Max(9,top.Height*5) &&
                Math.Abs(c.CenterX-top.CenterX)<Math.Max(2,(c.Width+top.Width)*0.35f) && c.Width>=top.Width*0.85f && c.Width<=top.Width*5+2)
                .OrderBy(c=>c.Y).FirstOrDefault();
            if(lower==null) continue;
            int left=Math.Min(top.X,lower.X),right=Math.Max(top.X+top.Width,lower.X+lower.Width),bottom=lower.Y+lower.Height;
            int w=right-left,h=bottom-top.Y;
            if(h<6 || h<w*1.25f || h>w*7 || w>width*0.045f) continue;
            int whiteRows=0;
            for(int y=top.Y+top.Height;y<lower.Y;y++) {
                int whites=0;
                for(int x=left;x<right;x++) {
                    int p=y*stride+x*3; int b=pixels[p],g=pixels[p+1],r=pixels[p+2];
                    if(Math.Min(r,Math.Min(g,b))>135 && Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<45) whites++;
                }
                if(whites>=Math.Max(1,w*0.3f)) whiteRows++;
            }
            if(whiteRows<Math.Max(1,h*0.07f)) continue;
            // A cone may have several orange sections separated by white bands.
            // Extend to its actual base: projecting an upper stripe as the foot
            // would place the cone too far away on the road.
            var tail=lower;
            for(int band=0;band<2;band++) {
                var next=parts.Where(c=>c.Y>=tail.Y+tail.Height && c.Y-(tail.Y+tail.Height)<=Math.Max(5,tail.Width*2) &&
                    Math.Abs(c.CenterX-tail.CenterX)<Math.Max(2,(c.Width+tail.Width)*0.3f) && c.Width>=tail.Width*0.8f && c.Width<=tail.Width*4.5f+2)
                    .OrderBy(c=>c.Y).FirstOrDefault();
                if(next==null) break;
                tail=next; left=Math.Min(left,tail.X); right=Math.Max(right,tail.X+tail.Width); bottom=tail.Y+tail.Height;
            }
            w=right-left; h=bottom-top.Y;
            if(h>w*7) continue;
            float scale=region.Width/(float)width;
            var box=new ImageBox(region.X+left*scale,region.Y+top.Y*scale,region.X+right*scale,region.Y+bottom*scale);
            if(detections.Any(d=>Math.Abs((d.Box.Left+d.Box.Right-box.Left-box.Right)/2)<w*scale &&
                Math.Abs(d.Box.Bottom-box.Bottom)<h*scale)) continue;
            detections.Add(new("traffic cone",0.8f,box));
        }
        return detections;
    }
}
#endif
