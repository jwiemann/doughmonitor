using OpenCvSharp;

namespace SourdoughMonitor.Vision;

/// <summary>Finds the outer bottom edge of the jar's glass base from one frame, the same way in
/// every lighting: the lowest strong horizontal edge in the central part of the jar. At night
/// the lit base is a bright crescent whose lower rim is that edge; by day it is the dark rim of
/// the foot. The detection methods for the dough surface each find their own idea of the bottom
/// (the top of the crescent, the end of the dark body, the held value of an earlier frame), and
/// those differ by tens of pixels in one unmoved scene, which shifts every dough height and
/// the dough floor offset when the method changes with the light.</summary>
public static class JarBottomEstimator
{
    /// <summary>Weakest edge (mean vertical gradient of the central strip) accepted as the base.</summary>
    private const double MinEdgeStrength = 15;

    /// <summary>Estimated bottom row, or null when no clear base edge is in the lower part of the
    /// frame (or it touches the frame's bottom edge).</summary>
    public static int? Estimate(Mat gray, int left, int right)
    {
        var width = right - left;
        if (gray.Empty() || width < 20) return null;
        var x0 = Math.Clamp(left + width * 3 / 10, 0, gray.Width - 2);
        var x1 = Math.Clamp(left + width * 7 / 10, x0 + 1, gray.Width - 1);
        using var strip = gray[new Rect(x0, 0, x1 - x0, gray.Height)];
        using var blurred = new Mat();
        Cv2.GaussianBlur(strip, blurred, new Size(0, 0), 2.0);
        using var blurredF = new Mat();
        blurred.ConvertTo(blurredF, MatType.CV_32F);
        using var gradient = new Mat();
        Cv2.Sobel(blurredF, gradient, MatType.CV_32F, 0, 1, 3);
        using var magnitude = new Mat();
        Cv2.Absdiff(gradient, Scalar.All(0), magnitude);
        using var magnitude8 = new Mat();
        magnitude.ConvertTo(magnitude8, MatType.CV_8U);
        if (!magnitude8.GetArray(out byte[] data)) return null;

        var columns = magnitude8.Cols;
        var profile = new double[gray.Height];
        var row = new byte[columns];
        for (var y = 0; y < gray.Height; y++)
        {
            Array.Copy(data, y * columns, row, 0, columns);
            Array.Sort(row);
            profile[y] = row[columns / 2];
        }

        var from = (int)(gray.Height * 0.55);
        var to = gray.Height - 3;
        var strongest = 0.0;
        for (var y = from; y <= to; y++) strongest = Math.Max(strongest, profile[y]);
        if (strongest < MinEdgeStrength) return null;
        for (var y = to; y >= from; y--)
        {
            if (profile[y] < 0.5 * strongest) continue;
            return y >= gray.Height - 3 ? null : y;
        }
        return null;
    }
}
