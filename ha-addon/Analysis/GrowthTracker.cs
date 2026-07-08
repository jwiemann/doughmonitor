using SourdoughMonitor.Config;

namespace SourdoughMonitor.Analysis;

public sealed class GrowthTracker(GrowthOptions options)
{
    private readonly List<GrowthSample> _samples = [];
    private readonly Queue<double> _medianWindow = new();

    public IReadOnlyList<GrowthSample> Samples => _samples;

    /// <summary>Feeds one raw measurement. Returns the accepted smoothed sample,
    /// or null if the measurement was rejected as an outlier.</summary>
    public GrowthSample? Add(LevelMeasurement measurement)
    {
        var rawHeight = measurement.DoughHeightPx;
        // 1. Physical plausibility gate against the last accepted sample:
        //    dough does not move faster than MaxRisePxPerMinute in either direction.
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            var minutes = (measurement.Time - last.Timestamp).TotalMinutes;
            if (minutes <= 0) return null;
            var maxDelta = options.MaxRisePxPerMinute * minutes + options.JitterTolerancePx;
            if (Math.Abs(rawHeight - last.HeightPx) > maxDelta)
                return null; // condensation streak, mis-detected run, camera artifact
        }
        // 2. Rolling median for jitter suppression.
        _medianWindow.Enqueue(rawHeight);
        while (_medianWindow.Count > options.MedianWindowSize)
            _medianWindow.Dequeue();
        var smoothed = Median(_medianWindow);
        var sample = new GrowthSample(measurement.Time, smoothed);
        _samples.Add(sample);
        TrimOldSamples(measurement.Time);
        return sample;
    }

    /// <summary>Analyzes the accepted samples. Peak prediction becomes available once the
    /// curve has enough shape for the logistic fit to converge (typically 45-60 min of rising).
    /// Trend and target ETA become available earlier.</summary>
    public GrowthAnalysis? Analyze()
    {
        if (_samples.Count == 0) return null;
        var start = _samples[0];
        var current = _samples[^1];
        var riseRate = EstimateRiseRate(options.RateWindow, endOffset: TimeSpan.Zero);
        var (acceleration, trend) = EstimateAcceleration(riseRate);
        var growthFactor = start.HeightPx > 0 ? current.HeightPx / start.HeightPx : 1.0;
        if (_samples.Count < 3)
        {
            return new GrowthAnalysis(
                current.HeightPx,
                start.HeightPx,
                growthFactor,
                riseRate,
                acceleration,
                trend,
                null,
                null,
                null,
                GrowthPhase.Collecting);
        }
        var fit = _samples.Count >= options.MinSamplesForFit ? FitLogistic() : null;
        var phase = ClassifyPhase(riseRate, fit, current);
        DateTimeOffset? peakTime = null;
        double? peakHeight = null;
        if (fit is not null && phase is GrowthPhase.Rising or GrowthPhase.Slowing)
        {
            // Practical peak: the time the curve reaches PeakFraction (e.g. 97%) of the
            // fitted plateau. The true asymptote is never reached, and real dough starts
            // collapsing before it, so a slightly earlier target is the useful answer.
            var t = fit.Value.TimeAtFraction(options.PeakFraction);
            if (t is not null)
            {
                peakTime = start.Timestamp + TimeSpan.FromHours(t.Value);
                peakHeight = fit.Value.Plateau * options.PeakFraction;
            }
        }
        var targetTime = EstimateTargetTime(start, current, riseRate, fit);
        return new GrowthAnalysis(
            current.HeightPx,
            start.HeightPx,
            growthFactor,
            riseRate,
            acceleration,
            trend,
            peakTime,
            peakHeight,
            targetTime,
            phase);
    }

    /// <summary>ETA for reaching TargetGrowthFactor (e.g. 1.75x for bulk fermentation).
    /// Prefers the logistic fit when available; falls back to linear extrapolation of the
    /// current rise rate, which is honest much earlier than the sigmoid converges.</summary>
    private DateTimeOffset? EstimateTargetTime(
        GrowthSample start,
        GrowthSample current,
        double riseRate,
        LogisticFit? fit)
    {
        var targetHeight = start.HeightPx * options.TargetGrowthFactor;
        if (current.HeightPx >= targetHeight) return current.Timestamp; // already there
        if (fit is not null && fit.Value.Plateau > targetHeight)
        {
            var t = fit.Value.TimeAtHeight(targetHeight);
            if (t is not null) return start.Timestamp + TimeSpan.FromHours(t.Value);
        }
        if (riseRate <= options.FlatRatePxPerHour) return null; // no meaningful rise, no ETA
        var hours = (targetHeight - current.HeightPx) / riseRate;
        if (hours > options.MaxEtaHorizon.TotalHours) return null; // too far out to be credible
        return current.Timestamp + TimeSpan.FromHours(hours);
    }

    /// <summary>Rise rate in px/h via least-squares slope over a window ending
    /// endOffset before the newest sample.</summary>
    private double EstimateRiseRate(TimeSpan window, TimeSpan endOffset)
    {
        var end = _samples[^1].Timestamp - endOffset;
        var startCutoff = end - window;
        var slice = _samples.Where(s => s.Timestamp >= startCutoff && s.Timestamp <= end)
            .ToArray();
        if (slice.Length < 2) return 0;
        return LinearSlope(slice, slice[0].Timestamp);
    }

    /// <summary>Acceleration in px/h² by comparing the current rate window against the
    /// preceding one; classified into a trend with a relative + absolute dead band.</summary>
    private (double Acceleration, RiseTrend Trend) EstimateAcceleration(double currentRate)
    {
        var span = _samples.Count > 0 ? _samples[^1].Timestamp - _samples[0].Timestamp : TimeSpan.Zero;
        if (span < options.RateWindow * 2) return (0, RiseTrend.Unknown);
        var previousRate = EstimateRiseRate(options.RateWindow, endOffset: options.RateWindow);
        var windowHours = options.RateWindow.TotalHours;
        var acceleration = (currentRate - previousRate) / windowHours;
        // Dead band: ignore changes smaller than 15% of the current rate or the noise floor.
        var deadBand = Math.Max(options.FlatRatePxPerHour, Math.Abs(currentRate) * 0.15) / windowHours;
        var trend = acceleration > deadBand ? RiseTrend.Accelerating :
            acceleration < -deadBand ? RiseTrend.Decelerating : RiseTrend.Steady;
        return (acceleration, trend);
    }

    private GrowthPhase ClassifyPhase(double riseRate, LogisticFit? fit, GrowthSample current)
    {
        if (_samples.Count < options.MinSamplesForFit) return GrowthPhase.Collecting;
        if (Math.Abs(riseRate) <= options.FlatRatePxPerHour)
        {
            // Flat: lag phase if we never rose meaningfully, peaked if we did.
            var totalRise = current.HeightPx - _samples[0].HeightPx;
            return totalRise > options.JitterTolerancePx * 2 ? GrowthPhase.Peaked : GrowthPhase.Lag;
        }
        if (riseRate < 0) return GrowthPhase.Peaked;
        if (fit is not null && current.HeightPx >= fit.Value.Plateau * 0.85) return GrowthPhase.Slowing;
        return GrowthPhase.Rising;
    }

    /// <summary>Logistic model h(t) = L / (1 + exp(-k (t - t0))) fitted by grid search over
    /// (k, t0) with L swept relative to the observed maximum. Deliberately simple and
    /// allocation-light instead of Levenberg-Marquardt — sample counts are tiny (one per
    /// capture interval) and the parameter space is well-bounded for dough.</summary>
    private LogisticFit? FitLogistic()
    {
        var t0Ref = _samples[0].Timestamp;
        var times = _samples.Select(s => (s.Timestamp - t0Ref).TotalHours)
            .ToArray();
        var heights = _samples.Select(s => s.HeightPx)
            .ToArray();
        var hMax = heights.Max();
        var hMin = heights.Min();
        if (hMax - hMin < options.JitterTolerancePx * 2)
            return null; // no meaningful rise yet, fit would be degenerate
        var tMax = times[^1];
        LogisticFit? best = null;
        var bestError = double.PositiveInfinity;
        // Plateau between "barely above current max" and "triple rise", typical for sourdough.
        for (var plateau = hMax * 1.02; plateau <= hMax * 3.0; plateau *= 1.06)
        for (var k = 0.2; k <= 6.0; k *= 1.15) // steepness in 1/h
        for (var tMid = -1.0; tMid <= tMax + 8.0; tMid += Math.Max(0.1, tMax / 40.0))
        {
            double error = 0;
            for (var i = 0; i < times.Length; i++)
            {
                var predicted = plateau / (1 + Math.Exp(-k * (times[i] - tMid)));
                var d = predicted - heights[i];
                error += d * d;
            }
            if (error < bestError)
            {
                bestError = error;
                best = new LogisticFit(plateau, k, tMid);
            }
        }
        if (best is null) return null;
        // Reject fits that explain the data no better than a flat line — avoids
        // confident nonsense predictions during the lag phase.
        var mean = heights.Average();
        var flatError = heights.Sum(h => (h - mean) * (h - mean));
        return bestError < flatError * 0.5 ? best : null;
    }

    private void TrimOldSamples(DateTimeOffset now)
    {
        var cutoff = now - options.HistoryRetention;
        var firstKept = _samples.FindIndex(s => s.Timestamp >= cutoff);
        if (firstKept > 0) _samples.RemoveRange(0, firstKept);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order()
            .ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    private static double LinearSlope(IReadOnlyList<GrowthSample> samples, DateTimeOffset t0)
    {
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        foreach (var s in samples)
        {
            var x = (s.Timestamp - t0).TotalHours;
            sumX += x;
            sumY += s.HeightPx;
            sumXy += x * s.HeightPx;
            sumXx += x * x;
        }
        var n = samples.Count;
        var denominator = n * sumXx - sumX * sumX;
        return Math.Abs(denominator) < 1e-9 ? 0 : (n * sumXy - sumX * sumY) / denominator;
    }

    private readonly record struct LogisticFit(double Plateau, double K, double TMid)
    {
        public double? TimeAtFraction(double fraction)
        {
            if (fraction is <= 0 or >= 1) return null;
            return TMid - Math.Log(1.0 / fraction - 1.0) / K;
        }

        public double? TimeAtHeight(double height)
        {
            if (height <= 0 || height >= Plateau) return null;
            return TimeAtFraction(height / Plateau);
        }
    }
}