using MathNet.Numerics.Statistics;

namespace SourdoughMonitor.Analysis;

using Models;

/// <summary>Peak forecast from the shape of the rise rate, not from a saturating curve.
/// Measured dough does not approach a plateau asymptotically: its rate ramps up, holds near a
/// maximum and then falls roughly linearly to zero, so the rise ends abruptly (observed
/// 2026-10-13/14: rate 19 and 24 %/h falling to zero within 1.4 h and 2.2 h). Asymptotic
/// sigmoid fits overshoot such a curve by hours (ETA 13:13 for a 02:35 peak).
/// The model: the rate grows linearly up to a maximum R at time t_m, then falls to zero within D
/// hours (slightly convex, see <see cref="Shapes"/>); the peak is at t_m + D and its height is the
/// rise at t_m plus the area under the falling rate. For every
/// (t_m, D) on a grid the remaining parameters (rise at t_m, R, growth before t_m) follow by
/// least squares on robust (median) buckets of the unsmoothed rise. The fit errors weight the
/// grid into a posterior, whose weighted percentiles are the earliest/latest times and heights.
/// While the rate has not yet fallen, t_m and D are not identified, the posterior stays wide and
/// no forecast is made.</summary>
public static class PeakForecaster
{
    /// <summary>Trailing span of readings the rate model is fitted to.</summary>
    private const int WindowMinutes = 240;

    /// <summary>Readings are reduced to one median per bucket, which also makes the fit independent
    /// of the sampling interval.</summary>
    private const int BucketMinutes = 10;

    private const int MinBuckets = 8;
    private const double MaxHoursToPeak = 12;

    /// <summary>The scatter of the buckets around the best fit is correlated over minutes and the
    /// rate model is only an approximation of real dough, so the fit error is scaled up before it
    /// weights the grid for the earliest/latest range (the point estimate uses the plain error).</summary>
    private const double NoiseInflation = 1.4;

    /// <summary>A forecast needs the rate maximum to lie at least this many hours in the past ...</summary>
    private const double MomentPastHours = 0.25;

    /// <summary>... with at least this posterior probability to start a forecast, and this one to
    /// keep an already running forecast alive through a noisy cycle.</summary>
    private const double StartPastProbability = 0.65;

    private const double ContinuePastProbability = 0.5;

    /// <summary>Widest earliest-to-latest span (hours) a forecast may have to start, and to continue.
    /// A wider posterior is dominated by the grid, not by the data.</summary>
    private const double StartRangeHours = 2.75;

    private const double ContinueRangeHours = 4;

    /// <summary>Exponents of the decline, rate = R·(1 − u/D)^shape, averaged by the posterior.
    /// Measured declines range from linear to slightly convex (flattening towards zero).</summary>
    private static readonly double[] Shapes = [1.0, 1.5, 2.0];

    /// <summary>The dough counts as peaked when the modelled rate is down to this fraction of its
    /// maximum.</summary>
    private const double PeakRateFraction = 0.02;

    // The grid of (rate-maximum time, decline duration) the model is fitted on, in hours from now.
    // The rate maximum may lie up to 1.5 h ahead (rate not yet turned), the decline lasts 0.5–3.5 h.
    private const double MomentFromHours = -3.5;
    private const double MomentStepHours = 1.0 / 6;
    private const int MomentSteps = 30;
    private const double DeclineFromHours = 0.5;
    private const double DeclineStepHours = 0.25;
    private const int DeclineSteps = 12;

    private readonly record struct Bucket(double X, double Y);

    private readonly record struct Cell(double Hours, double Rise, double Sse, double Moment);

    /// <summary>The fitted peak: ahead of now while the rise is still turning, or already behind
    /// (<see cref="RateForecast.HoursToPeak"/> at or below zero) once the rise has stopped. The
    /// rate-based "peaked" rule needs its slope window to flatten, 1.5 h after the stop on
    /// 2026-10-15; until then the fit's estimate of when and at what rise the dough stopped is the
    /// answer, instead of an empty sensor.</summary>
    public static RateForecast? TryForecast(
        IReadOnlyList<Sample> samples, DateTimeOffset now, double noiseFloorPercent, bool continuing)
    {
        var buckets = Bucketize(samples, now);
        if (buckets.Count < MinBuckets) return null;

        var cells = new List<Cell>((MomentSteps + 1) * (DeclineSteps + 1) * Shapes.Length);
        for (var i = 0; i <= MomentSteps; i++)
            for (var j = 0; j <= DeclineSteps; j++)
                foreach (var shape in Shapes)
                    if (Fit(buckets, MomentFromHours + i * MomentStepHours,
                            DeclineFromHours + j * DeclineStepHours, shape) is { } cell)
                        cells.Add(cell);
        if (cells.Count == 0) return null;

        var best = cells.Min(c => c.Sse);
        var degreesOfFreedom = Math.Max(1, buckets.Count - 4);
        var sigma = Math.Max(noiseFloorPercent, Math.Sqrt(best / degreesOfFreedom));
        double[] Posterior(double scale) =>
            cells.Select(c => Math.Exp(-(c.Sse - best) / (2 * scale * scale * sigma * sigma))).ToArray();
        var widened = Posterior(NoiseInflation);
        var fitted = Posterior(1);
        var peakHours = cells.Select(c => c.Hours).ToArray();
        var peakRises = cells.Select(c => c.Rise).ToArray();
        var hoursRange = WeightedQuantiles(peakHours, widened);
        var risesRange = WeightedQuantiles(peakRises, widened);
        // The point estimate follows the better-fitting models more closely than the range does.
        var pointHours = WeightedQuantiles(peakHours, fitted).Median;
        var pointRise = WeightedQuantiles(peakRises, fitted).Median;

        var pastMass = 0.0;
        for (var i = 0; i < cells.Count; i++)
            if (cells[i].Moment <= -MomentPastHours) pastMass += widened[i];
        var pastProbability = pastMass / widened.Sum();
        // The rate has not demonstrably fallen yet: its maximum could still lie ahead.
        if (pastProbability < (continuing ? ContinuePastProbability : StartPastProbability)) return null;
        var (low, high) = (Math.Min(hoursRange.Low, pointHours), Math.Max(hoursRange.High, pointHours));
        if (high > MaxHoursToPeak || high - low > (continuing ? ContinueRangeHours : StartRangeHours)) return null;
        var riseLow = Math.Min(risesRange.Low, pointRise);
        var riseHigh = Math.Max(risesRange.High, pointRise);
        // Behind us the fit is only stated for a forecast already running: the turn was seen
        // coming, and the rise now confirms it stopped. On its own a flat or creeping stretch
        // (2.6 %/h for 2.5 h on 2026-10-14) fits "stopped" just as well and would publish a false
        // peak. It must also be certain the rate is gone.
        if (pointHours <= 0.05)
            return continuing && pastProbability >= PeakBehindProbability && high <= PeakBehindRangeHours
                ? new RateForecast(pointHours, low, high, pointRise, riseLow, riseHigh)
                : null;
        return new RateForecast(pointHours, Math.Max(0, low), high, pointRise, riseLow, riseHigh);
    }

    /// <summary>Share of the models (by weight) whose rate maximum lies past, and the latest the
    /// peak may lie ahead, for the fit to state a peak that is already behind us.</summary>
    private const double PeakBehindProbability = 0.95;

    private const double PeakBehindRangeHours = 1.0;

    /// <summary>Least-squares fit of the rate model for a fixed rate-maximum time
    /// <paramref name="momentHours"/> and decline duration <paramref name="declineHours"/> (both
    /// relative to now). Returns the peak (hours from now, rise) and the squared error, or null
    /// when no non-negative rate fits.</summary>
    private static Cell? Fit(List<Bucket> buckets, double momentHours, double declineHours, double shape)
    {
        // rise(x) = r + R·phi(x) + g·chi(x); u is the time since the rate maximum.
        double s00 = 0, s01 = 0, s02 = 0, s11 = 0, s12 = 0, s22 = 0, b0 = 0, b1 = 0, b2 = 0, yy = 0;
        foreach (var (x, y) in buckets)
        {
            var u = x - momentHours;
            double phi, chi;
            if (u < 0) { phi = u; chi = u * u / 2; }
            else if (u <= declineHours) { phi = declineHours / (shape + 1) * (1 - Math.Pow(1 - u / declineHours, shape + 1)); chi = 0; }
            else { phi = declineHours / (shape + 1); chi = 0; }
            s00 += 1; s01 += phi; s02 += chi; s11 += phi * phi; s12 += phi * chi; s22 += chi * chi;
            b0 += y; b1 += phi * y; b2 += chi * y; yy += y * y;
        }
        if (Solve3(s00, s01, s02, s11, s12, s22, b0, b1, b2) is { } full && full.R > 0 && full.G >= 0)
            return Peak(full.R0, full.R, yy, full.Sol, momentHours, declineHours, shape);
        // Growth before the maximum dropped to zero: constant rate up to it.
        if (Solve2(s00, s01, s11, b0, b1) is { } flat && flat.R > 0)
            return Peak(flat.R0, flat.R, yy, flat.Sol, momentHours, declineHours, shape);
        return null;
    }

    private static Cell Peak(double riseAtMoment, double rate, double yy, double[] solution,
        double momentHours, double declineHours, double shape)
    {
        // Squared error from the normal equations: yy - solution · b.
        var sse = Math.Max(0, yy - solution[3]);
        // The rate only reaches zero asymptotically for a convex decline; the dough has peaked once
        // the rate is down to a small fraction of its maximum. The rise gained by then:
        // R·D/(shape+1) · (1 − fraction^((shape+1)/shape)).
        var peakHours = momentHours + declineHours * (1 - Math.Pow(PeakRateFraction, 1 / shape));
        var peakRise = riseAtMoment
                       + rate * declineHours / (shape + 1) * (1 - Math.Pow(PeakRateFraction, (shape + 1) / shape));
        return new Cell(peakHours, peakRise, sse, momentHours);
    }

    private static (double R0, double R, double G, double[] Sol)? Solve3(
        double s00, double s01, double s02, double s11, double s12, double s22, double b0, double b1, double b2)
    {
        var a = new[,] { { s00, s01, s02, b0 }, { s01, s11, s12, b1 }, { s02, s12, s22, b2 } };
        if (!Eliminate(a, 3)) return null;
        var (r0, r, g) = (a[0, 3], a[1, 3], a[2, 3]);
        return (r0, r, g, [r0, r, g, r0 * b0 + r * b1 + g * b2]);
    }

    private static (double R0, double R, double[] Sol)? Solve2(
        double s00, double s01, double s11, double b0, double b1)
    {
        var a = new[,] { { s00, s01, b0 }, { s01, s11, b1 } };
        if (!Eliminate(a, 2)) return null;
        var (r0, r) = (a[0, 2], a[1, 2]);
        return (r0, r, [r0, r, 0, r0 * b0 + r * b1]);
    }

    /// <summary>Gauss-Jordan elimination with partial pivoting on an augmented n×(n+1) matrix;
    /// leaves the solution in the last column. False when singular.</summary>
    private static bool Eliminate(double[,] a, int n)
    {
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-12) return false;
            if (pivot != col)
                for (var k = 0; k <= n; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            for (var row = 0; row < n; row++)
            {
                if (row == col) continue;
                var factor = a[row, col] / a[col, col];
                for (var k = col; k <= n; k++) a[row, k] -= factor * a[col, k];
            }
        }
        for (var row = 0; row < n; row++) a[row, n] /= a[row, row];
        return true;
    }

    private static (double Low, double Median, double High) WeightedQuantiles(double[] values, double[] weights)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var total = weights.Sum();
        double Quantile(double q)
        {
            var target = q * total;
            var cumulative = 0.0;
            foreach (var i in order)
            {
                cumulative += weights[i];
                if (cumulative >= target) return values[i];
            }
            return values[order[^1]];
        }
        return (Quantile(0.1), Quantile(0.5), Quantile(0.9));
    }

    /// <summary>Median of the unsmoothed rise per time bucket with the bucket's mean time in
    /// hours relative to now (negative = past).</summary>
    private static List<Bucket> Bucketize(IReadOnlyList<Sample> samples, DateTimeOffset now)
    {
        var groups = new Dictionary<int, List<(double X, double Y)>>();
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            var minutesAgo = (now - samples[i].Time).TotalMinutes;
            if (minutesAgo >= WindowMinutes) break;
            var bucket = Math.Max(0, (int)(minutesAgo / BucketMinutes));
            if (!groups.TryGetValue(bucket, out var list)) groups[bucket] = list = [];
            list.Add((-minutesAgo / 60.0, samples[i].RawRisePercent ?? samples[i].RisePercent));
        }
        return groups.OrderBy(g => g.Key)
            .Select(g => new Bucket(g.Value.Average(p => p.X), g.Value.Select(p => p.Y).Median()))
            .ToList();
    }
}
