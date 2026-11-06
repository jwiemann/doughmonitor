using OpenCvSharp;

namespace SourdoughMonitor.Vision;

/// <summary>Pixel-space settings of the wall-contact measurement. The defaults are tuned for the
/// current camera framing (1280x720, jar body spanning rows ~300-600); they are not scaled.</summary>
public sealed record WallContactOptions
{
    /// <summary>Row band searched for the dough; clear glass must lie above its first rows.</summary>
    public int TopRow { get; init; } = 300;

    public int BottomRow { get; init; } = 600;

    /// <summary>Dough starts where the band's mean gray falls below this fraction of the clear
    /// glass level above it, and stays there.</summary>
    public double DropFraction { get; init; } = 0.75;

    /// <summary>Strip offsets (px inside the outer glass wall); the contact is lowest at the wall.</summary>
    public int[] NearOffsets { get; init; } = [3, 6, 9, 12];

    /// <summary>Fallback offsets when the near strips give no clean contact.</summary>
    public int[] FarOffsets { get; init; } = [14, 24, 34];

    public int StripWidth { get; init; } = 5;

    /// <summary>A strip deviating more than this from its side's median is dropped.</summary>
    public double OutlierPx { get; init; } = 8;

    /// <summary>Plausible outer jar width; classic extents outside it are replaced by the last plausible ones.</summary>
    public int MinJarWidth { get; init; } = 240;

    public int MaxJarWidth { get; init; } = 300;
}

/// <summary>Dough top over the jar interior and the dough/glass contact rows at the left and right wall.</summary>
/// <param name="DomeTopRow">Topmost dough row over the jar interior.</param>
/// <param name="SurfaceMedianRow">Median dough-start row over the same columns; its distance below
/// <paramref name="DomeTopRow"/> measures how ragged the surface is (smooth dome: 6-11 px, ragged lump: 17-21 px).</param>
public sealed record WallContacts(double? DomeTopRow, double? LeftRow, double? RightRow, double? SurfaceMedianRow = null);

/// <summary>Measures where the dough meets the glass wall and the topmost dough row. Image y grows
/// downward. Stateless and pure: it reads an 8-bit single-channel frame and the jar's outer wall columns.</summary>
public static class WallContactMeasurer
{
    public static WallContacts Measure(Mat gray, int left, int right, WallContactOptions? options = null)
    {
        options ??= new WallContactOptions();
        var leftRow = Side(gray, left, +1, options);
        var rightRow = Side(gray, right, -1, options);
        var (top, median) = DomeTopAndMedian(gray, left, right, options);
        return new WallContacts(top, leftRow, rightRow, median);
    }

    /// <summary>First row (from <see cref="WallContactOptions.TopRow"/>) where the column band turns dark
    /// relative to the clear glass above it. Null when no sustained drop exists.</summary>
    public static int? FirstDarkRow(Mat gray, int x0, int x1, WallContactOptions options)
    {
        x0 = Math.Max(x0, 0);
        x1 = Math.Min(x1, gray.Cols);
        var rows = Math.Min(options.BottomRow, gray.Rows) - options.TopRow;
        if (rows < 40 || x1 <= x0) return null;
        var profile = new double[rows];
        for (var i = 0; i < rows; i++)
        {
            double sum = 0;
            for (var x = x0; x < x1; x++) sum += gray.At<byte>(options.TopRow + i, x);
            profile[i] = sum / (x1 - x0);
        }
        var smooth = Smooth(profile, 5);
        var reference = Median(smooth.AsSpan(0, 25).ToArray());
        var threshold = reference * options.DropFraction;
        for (var i = 10; i < rows - 12; i++)
        {
            if (smooth[i] >= threshold) continue;
            var sustained = true;
            for (var k = i; k < i + 12; k++)
            {
                if (smooth[k] >= threshold * 1.08)
                {
                    sustained = false;
                    break;
                }
            }
            if (sustained) return options.TopRow + i;
        }
        return null;
    }

    /// <summary>Topmost dough row over the jar interior (and the median dough-start row): per-column dough
    /// start (4 px columns), 3-column median against single-column glints, then the minimum.</summary>
    public static (double? Top, double? Median) DomeTopAndMedian(Mat gray, int left, int right, WallContactOptions options)
    {
        var ys = new List<double>();
        for (var x = left + 14; x < right - 14; x += 4)
        {
            if (FirstDarkRow(gray, x, x + 4, options) is { } y) ys.Add(y);
        }
        if (ys.Count < 12) return (null, null);
        var smooth = new double[ys.Count];
        for (var i = 0; i < ys.Count; i++)
        {
            var from = Math.Max(0, i - 1);
            smooth[i] = Median(ys.Skip(from).Take(i - from + 2).ToArray());
        }
        return (smooth.Min(), Median(smooth));
    }

    private static double? Side(Mat gray, int edge, int sign, WallContactOptions options)
    {
        foreach (var offsets in new[] { options.NearOffsets, options.FarOffsets })
        {
            var rows = new List<double>();
            foreach (var o in offsets)
            {
                var x0 = sign > 0 ? edge + o : edge - o - options.StripWidth;
                var contact = FirstDarkRow(gray, x0, x0 + options.StripWidth, options);
                if (contact is { } c) rows.Add(c);
            }
            if (rows.Count < 2) continue;
            var median = Median(rows.ToArray());
            var kept = rows.Where(v => Math.Abs(v - median) <= options.OutlierPx).ToArray();
            if (kept.Length >= 2) return Median(kept);
        }
        return null;
    }

    private static double[] Smooth(double[] values, int window)
    {
        // Same-length moving average with zero padding at the ends (NumPy convolve "same").
        var result = new double[values.Length];
        var half = window / 2;
        for (var i = 0; i < values.Length; i++)
        {
            double sum = 0;
            for (var k = -half; k <= half; k++)
            {
                var j = i + k;
                if (j >= 0 && j < values.Length) sum += values[j];
            }
            result[i] = sum / window;
        }
        return result;
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }
}
