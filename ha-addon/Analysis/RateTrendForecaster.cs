using MathNet.Numerics.Statistics;

namespace SourdoughMonitor.Analysis;

using Models;

/// <summary>Peak forecast from the trend of the rise rate (the derivative), not from a
/// saturating curve. Measured dough does not approach a plateau asymptotically: its rate
/// ramps up, passes a maximum and then falls roughly linearly to zero, so the rise peaks where
/// the extrapolated rate reaches zero. Asymptotic sigmoid/Gompertz/Richards fits overshoot
/// such a curve by hours (observed 2026-10-13: ETA 13:13 for a 02:35 peak).</summary>
public static class RateTrendForecaster
{
    /// <summary>Trailing spans (minutes) the rate trend is fitted over. Their disagreement is
    /// the forecast's uncertainty range.</summary>
    private static readonly int[] WindowMinutes = [75, 90, 120];

    private const int BucketMinutes = 5;
    private const int MinBuckets = 6;
    private const int MinAgreeingWindows = 2;
    private const double MaxHoursToPeak = 12;

    /// <summary>Extrapolates the declining rate to zero. Each window fits a robust
    /// (Theil–Sen) line through the five-minute mean rates; it contributes only when the
    /// line is still positive now and falling. Returns null unless at least
    /// <see cref="MinAgreeingWindows"/> windows contribute.</summary>
    public static RateForecast? TryForecast(
        IReadOnlyList<SlopeSample> slopes, double currentRisePercent, DateTimeOffset now)
    {
        List<(double Hours, double Rise)> estimates = [];
        foreach (var window in WindowMinutes)
        {
            if (Estimate(slopes, now, window) is not { } line) continue;
            var (rateNow, slopePerHour) = line;
            var hours = rateNow / -slopePerHour;
            if (hours > MaxHoursToPeak) continue;
            // Triangle under the rate line from now until it hits zero.
            estimates.Add((hours, currentRisePercent + rateNow * rateNow / (2 * -slopePerHour)));
        }
        if (estimates.Count < MinAgreeingWindows) return null;
        var hoursList = estimates.Select(e => e.Hours).ToList();
        var riseList = estimates.Select(e => e.Rise).ToList();
        return new RateForecast(
            hoursList.Median(), hoursList.Min(), hoursList.Max(),
            riseList.Median(), riseList.Min(), riseList.Max());
    }

    /// <summary>The fitted rate now (%/h) and its slope (%/h per hour), when falling.</summary>
    private static (double RateNow, double SlopePerHour)? Estimate(
        IReadOnlyList<SlopeSample> slopes, DateTimeOffset now, int windowMinutes)
    {
        var sums = new Dictionary<int, (double Sum, int Count)>();
        for (var i = slopes.Count - 1; i >= 0; i--)
        {
            var minutesAgo = (now - (slopes[i].Centre ?? slopes[i].Time)).TotalMinutes;
            if (minutesAgo >= windowMinutes) continue;
            var bucket = Math.Max(0, (int)(minutesAgo / BucketMinutes));
            sums.TryGetValue(bucket, out var s);
            sums[bucket] = (s.Sum + slopes[i].Slope, s.Count + 1);
        }
        if (sums.Count < MinBuckets) return null;
        var x = new List<double>(sums.Count);
        var y = new List<double>(sums.Count);
        foreach (var (bucket, s) in sums)
        {
            x.Add(-(bucket + 0.5) * BucketMinutes / 60.0);
            y.Add(s.Sum / s.Count);
        }
        var pairSlopes = new List<double>();
        for (var i = 0; i < x.Count; i++)
            for (var j = i + 1; j < x.Count; j++)
                pairSlopes.Add((y[j] - y[i]) / (x[j] - x[i]));
        var slope = pairSlopes.Median();
        var rateNow = y.Select((v, i) => v - slope * x[i]).Median();
        return slope < 0 && rateNow > 0 ? (rateNow, slope) : null;
    }
}
