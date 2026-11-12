using OpenCvSharp;

namespace SourdoughMonitor.Vision;

/// <summary>Snaps the vision model's surface row to the dough's silhouette when the scene is backlit: a
/// backlit dough is a black body below a lit glass interior, and its top edge is a step of tens of
/// grey levels that the model can only place to within some pixels (2026-11-12, bread bowl: samples
/// 304-320 px for a mound whose edge was sharp to a pixel; one sample 354). The model still finds the
/// jar and the rough level; this only moves the level to the edge within a window around it, and does
/// nothing when no strong light-to-black step is there (daylight, pale dough).</summary>
public static class SilhouetteRefiner
{
    /// <summary>The body below the edge must be at most this share of the level above (a lit room: 114 below
    /// 154; dark: 5 below 80).</summary>
    private const double MaxBodyShare = 0.8;

    /// <summary>Minimum brightness step across the edge.</summary>
    private const double MinStep = 25;

    private const int Strips = 7;
    private const int MinValidStrips = 4;
    private const int Band = 6;

    /// <summary>The refined level (mean of the edge rows at seven columns over the central 60 % of the jar
    /// width, as the model's own level is defined) or null when the silhouette is not clear.
    /// <paramref name="gray"/> is the crop the jar box was drawn in; <paramref name="cropX"/> its x offset
    /// in the full frame, <paramref name="cropY"/> its y offset; rows are full-frame.</summary>
    public static double? Refine(Mat gray, JarBox jar, int cropX, int cropY, double modelRow)
    {
        modelRow -= cropY;
        var window = Math.Max(20, (int)Math.Round(0.12 * gray.Height));
        var lo = Math.Max(Band + 4, (int)Math.Round(modelRow) - window);
        var hi = Math.Min(gray.Height - Band - 5, (int)Math.Round(modelRow) + window);
        if (hi <= lo) return null;
        var left = jar.Left - cropX + 0.2 * (jar.Right - jar.Left);
        var right = jar.Right - cropX - 0.2 * (jar.Right - jar.Left);
        var stripWidth = Math.Max(8, (int)Math.Round((right - left) / (Strips * 2.0)));
        var rows = new List<double>();
        for (var s = 0; s < Strips; s++)
        {
            var centre = left + (right - left) * s / (Strips - 1);
            var x = (int)Math.Round(centre - stripWidth / 2.0);
            x = Math.Clamp(x, 0, gray.Width - stripWidth);
            using var strip = gray[new Rect(x, 0, stripWidth, gray.Height)];
            using var column = new Mat();
            Cv2.Reduce(strip, column, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);
            var profile = new float[gray.Height];
            column.GetArray(out float[] values);
            Array.Copy(values, profile, Math.Min(values.Length, profile.Length));
            if (Edge(profile, lo, hi) is { } edge) rows.Add(edge);
        }
        return rows.Count >= MinValidStrips ? rows.Average() + cropY : null;
    }

    private static int? Edge(float[] profile, int lo, int hi)
    {
        var bestStep = 0.0;
        var best = -1;
        var bestBelow = 0.0;
        var bestAbove = 0.0;
        for (var y = lo; y <= hi; y++)
        {
            double above = 0, below = 0;
            for (var i = 0; i < Band; i++)
            {
                above += profile[y - 3 - i];
                below += profile[y + 3 + i];
            }
            above /= Band;
            below /= Band;
            var step = above - below;
            if (step <= bestStep) continue;
            bestStep = step;
            best = y;
            bestBelow = below;
            bestAbove = above;
        }
        return best >= 0 && bestStep >= MinStep && bestBelow <= MaxBodyShare * bestAbove ? best : null;
    }
}
