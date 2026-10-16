using System.Text.Json;
using MathNet.Numerics.Statistics;

using SourdoughMonitor.Config;

namespace SourdoughMonitor.Analysis;

using Models;

/// <summary>Stateful per-session analysis: baseline tracking, auto-reset, rolling slope, sigmoid-based ETA.</summary>
public sealed class RiseAnalyzer
{
    /// <summary>Fewest samples a rate may be fitted from, whatever their error estimate.</summary>
    private const int MinRateSamples = 4;

    /// <summary>Fewest accepted samples a session needs to be summarised when it ends.</summary>
    private const int MinSamplesForSummary = 5;

    private readonly AnalysisOptions _options;
    private readonly List<Sample> _samples = [];
    private readonly List<SlopeSample> _slopes = [];
    private readonly Queue<double> _heightWindow = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private double? _baselineDoughHeightPx;
    private bool _baselineOnDoughFloor;
    private DateTimeOffset _sessionStart;
    private bool _peaked;
    private double? _lastAcceptedHeightPx;
    private DateTimeOffset? _lastMeasurementTime;
    private int _implausibleStreak;
    private readonly List<double> _startHeights = [];
    private List<double>? _pendingCollapseHeights;
    private double _maximumSlope;
    private double? _peakReferenceRisePercent;
    private DateTimeOffset? _lastPublishedEta;
    private readonly List<PublishedForecast> _recentForecasts = [];
    private SessionSummary? _pendingSummary;

    public RiseAnalyzer(AnalysisOptions options)
    {
        _options = options;
        RestoreState();
    }

    /// <summary>The dough height (px) the current session's rise percentage is measured
    /// against, or null when there is no active session. Used to draw a "session start"
    /// reference line on the debug image.</summary>
    public double? BaselineDoughHeightPx => _baselineDoughHeightPx;

    public RiseReading Reset()
    {
        var now = DateTimeOffset.UtcNow;
        CaptureSessionSummary(now);
        ResetSession(DateTimeOffset.MinValue, null);
        SaveState();
        return new RiseReading(now, 0, null, null, null, null, null, false, NewSession: true);
    }

    public RiseReading? Analyze(LevelMeasurement m)
    {
        if (_lastMeasurementTime is { } lastTime && m.Time <= lastTime) return null;
        if (_baselineDoughHeightPx is not { } baseline || SessionExpired(m.Time))
            return StartMeasuredSession(m.Time, m.DoughHeightPx, m.OnDoughFloor);
        if (_baselineOnDoughFloor != m.OnDoughFloor)
        {
            // A floor-based session cannot take a glass-bottom height (floor not measured
            // again yet, e.g. a restart without persisted geometry): unavailable, never mixed.
            if (!m.OnDoughFloor) return null;
            // The scene's floor was first measured mid-session. Convert the session to the
            // floor basis rather than restarting it, so the history survives.
            var rebased = RebaseToDoughFloor(m.JarBottomPx - m.DoughFloorPx!.Value);
            if (rebased is null) return StartMeasuredSession(m.Time, m.DoughHeightPx, true);
            baseline = rebased.Value;
        }

        // Confirm feeding/handling on raw heights before the median can blend the
        // discontinuity into otherwise valid fermentation samples and their rate.
        var rawRisePercent = (m.DoughHeightPx - baseline) / baseline * 100.0;
        if (IsCollapseReset(rawRisePercent, m.DoughHeightPx))
        {
            var pending = _pendingCollapseHeights ??= [];
            pending.Add(m.DoughHeightPx);
            if (pending.Count < _options.CollapseConfirmSamples) return null;
            return StartMeasuredSession(m.Time, pending.Median(), m.OnDoughFloor);
        }
        _pendingCollapseHeights?.Clear();
        if (IsImplausibleJump(m.DoughHeightPx, m.Time))
        {
            _implausibleStreak++;
            if (_implausibleStreak <= _options.MaxImplausibleJumpRejects) return null;
            // A misdetected frame does not repeat this many times: trust the persistent
            // level, e.g. overnight growth first seen at dawn. Genuine feeding drops were
            // already confirmed above, and the slope never bridges a gap beyond its window.
        }
        _implausibleStreak = 0;
        if (SettleBaseline(m) is { } settled)
        {
            baseline = settled;
            rawRisePercent = (m.DoughHeightPx - baseline) / baseline * 100.0;
        }
        var smoothedHeightPx = Smooth(m.DoughHeightPx);
        _lastAcceptedHeightPx = smoothedHeightPx;
        _lastMeasurementTime = m.Time;
        var risePercent = (smoothedHeightPx - baseline) / baseline * 100.0;
        _samples.Add(new Sample(m.Time, risePercent, rawRisePercent));
        var rateSample = ComputeRate(m.Time);
        double? slope = rateSample?.Slope;
        if (rateSample is not null)
        {
            _slopes.Add(rateSample);
            _maximumSlope = Math.Max(_maximumSlope, rateSample.Slope);
        }
        RateForecast? forecast = null;
        if (!_peaked && _samples.Count >= _options.MinSamplesForFit && ForecastIsInformative())
            forecast = PeakForecaster.TryForecast(
                _samples, m.Time, _options.RateNoiseFloorPx / baseline * 100.0 / 2,
                continuing: _recentForecasts.Count > 0
                            && (m.Time - _recentForecasts[^1].Time).TotalMinutes <= ContinuationMinutes);
        if (slope is not null) UpdatePeakState();
        if (_peaked) forecast = null;
        DateTimeOffset? eta = null, etaLow = null, etaHigh = null;
        double? peakRise = null;
        if (!_peaked && Publish(m.Time, forecast) is { } published)
            (eta, etaLow, etaHigh, peakRise) = (published.Point, published.Low, published.High, published.Rise);
        SaveState();
        return new RiseReading(
            m.Time,
            Math.Round(ClampRisePercent(risePercent), 1),
            slope is null ? null : Math.Round(slope.Value, 1),
            peakRise is null ? null : Math.Round(ClampPredictedPeak(peakRise.Value), 0),
            eta,
            etaLow,
            etaHigh,
            _peaked,
            NewSession: false,
            SessionStart: _sessionStart);
    }

    /// <summary>The forecast as published: the median of the forecasts of the last few cycles, so
    /// one noisy cycle does not make the ETA jump. A cycle whose fit is momentarily undetermined
    /// keeps the previous forecast for a few minutes (while its ETA still lies ahead) instead of
    /// blanking the sensors. The range is the spread the forecasts report, widened to include the
    /// published point.</summary>
    private PublishedValues? Publish(DateTimeOffset now, RateForecast? forecast)
    {
        _recentForecasts.RemoveAll(f => (now - f.Time).TotalMinutes > ContinuationMinutes);
        if (forecast is not null)
            _recentForecasts.Add(new PublishedForecast(
                now, now.AddHours(forecast.HoursToPeak), now.AddHours(forecast.HoursLow),
                now.AddHours(forecast.HoursHigh), forecast.PeakRisePercent));
        // A fresh forecast may state a peak that already lies behind us; a held one only counts
        // while its ETA still lies ahead.
        var window = _recentForecasts
            .Where(f => (now - f.Time).TotalMinutes <= ForecastSmoothingMinutes && (forecast is not null || f.Eta > now))
            .ToList();
        if (window.Count == 0 || (forecast is null && (now - window[^1].Time).TotalMinutes > HoldMinutes))
            return null;
        DateTimeOffset MedianTime(Func<PublishedForecast, DateTimeOffset> pick) =>
            new((long)window.Select(f => (double)pick(f).UtcTicks).Median(), TimeSpan.Zero);
        var point = MedianTime(f => f.Eta);
        _lastPublishedEta = point;
        var (low, high) = (MedianTime(f => f.Low), MedianTime(f => f.High));
        return new PublishedValues(
            point, low < point ? low : point, high > point ? high : point, window.Select(f => f.Rise).Median());
    }

    /// <summary>Forecasts this many minutes back are medianed into the published one.</summary>
    private const double ForecastSmoothingMinutes = 15;

    /// <summary>How long the previous forecast is kept when a cycle yields none.</summary>
    private const double HoldMinutes = 10;

    /// <summary>A forecast counts as running when the last one is at most this many minutes old; a
    /// running forecast survives a noisier fit than a new one would.</summary>
    private const double ContinuationMinutes = 30;

    private sealed record PublishedForecast(
        DateTimeOffset Time, DateTimeOffset Eta, DateTimeOffset Low, DateTimeOffset High, double Rise);

    private sealed record PublishedValues(DateTimeOffset Point, DateTimeOffset Low, DateTimeOffset High, double Rise);

    private double SlopeEvidence => _options.MinSlopeEvidenceSigmas * _options.MaxRateStdErrPercentPerHour;

    /// <summary>A forecast needs evidence that the dough really rose: the prior minimum rise of a
    /// fed jar, and a maximum rate well above the rate's error bound. Whether the rate has already
    /// turned is decided by the forecaster from the shape of the recent rise.</summary>
    private bool ForecastIsInformative() =>
        _samples[^1].RisePercent >= Math.Max(_options.MinRisePercentForPeak, _options.MinRisePercentForForecast)
        && _slopes.Count > 0
        && _maximumSlope >= SlopeEvidence;

    private double Smooth(double rawHeightPx)
    {
        _heightWindow.Enqueue(rawHeightPx);
        while (_heightWindow.Count > _options.MedianWindowSize)
            _heightWindow.Dequeue();
        return _heightWindow.Median();
    }

    /// <summary>Domain gate for one raw reading. The dough is fed once a day and then follows a
    /// smooth curve: its level moves by at most <see cref="AnalysisOptions.MaxRisePercentPerHour"/>
    /// up and <see cref="AnalysisOptions.MaxFallPercentPerHour"/> down and never jumps, so a reading
    /// that sits far off the recent trend is a misdetection, not dough. The reference is the
    /// robust trend of the last accepted readings (the rate clamped to the physical limits),
    /// the allowed deviation the reading noise seen there; with too few readings the reference is
    /// the last accepted level and the allowance grows with the elapsed time at the fastest
    /// physical rate. Real events (feeding, handling) were confirmed before this gate.</summary>
    private bool IsImplausibleJump(double rawHeightPx, DateTimeOffset now)
    {
        if (_lastAcceptedHeightPx is null || _lastMeasurementTime is null
            || _baselineDoughHeightPx is not { } baseline || baseline <= 0) return false;
        var minutes = (now - _lastMeasurementTime.Value).TotalMinutes;
        if (minutes <= 0) return true; // non-advancing or out-of-order timestamp
        var risePxPerHour = _options.MaxRisePercentPerHour / 100.0 * baseline;
        var fallPxPerHour = _options.MaxFallPercentPerHour / 100.0 * baseline;
        if (ExpectedHeight(now, baseline, -fallPxPerHour, risePxPerHour) is { } expected)
            return Math.Abs(rawHeightPx - expected.HeightPx)
                   > Math.Max(_options.OutlierTolerancePx, OutlierScatterFactor * expected.ScatterPx);
        var budget = Math.Max(risePxPerHour, fallPxPerHour) / 60.0 * minutes + _options.OutlierTolerancePx;
        return Math.Abs(rawHeightPx - _lastAcceptedHeightPx.Value) > budget;
    }

    private const int TrendWindowMinutes = 45;
    private const int TrendMinReadings = 5;
    private const double TrendMinSpanMinutes = 20;
    private const double OutlierScatterFactor = 3;

    /// <summary>The level expected at <paramref name="now"/> from the robust (Theil-Sen) line through
    /// the unsmoothed levels of the last <see cref="TrendWindowMinutes"/>, with the scatter of
    /// those levels around it; null while fewer than <see cref="TrendMinReadings"/> readings
    /// spanning <see cref="TrendMinSpanMinutes"/> exist.</summary>
    private (double HeightPx, double ScatterPx)? ExpectedHeight(
        DateTimeOffset now, double baseline, double minPxPerHour, double maxPxPerHour)
    {
        var points = new List<(double Hours, double HeightPx)>();
        for (var i = _samples.Count - 1; i >= 0; i--)
        {
            var age = now - _samples[i].Time;
            if (age.TotalMinutes > TrendWindowMinutes) break;
            points.Add((-age.TotalHours, baseline * (1 + (_samples[i].RawRisePercent ?? _samples[i].RisePercent) / 100.0)));
        }
        if (points.Count < TrendMinReadings
            || (points[0].Hours - points[^1].Hours) * 60 < TrendMinSpanMinutes) return null;
        var slopes = new List<double>();
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
                if (points[i].Hours != points[j].Hours)
                    slopes.Add((points[i].HeightPx - points[j].HeightPx) / (points[i].Hours - points[j].Hours));
        var slope = Math.Clamp(slopes.Median(), minPxPerHour, maxPxPerHour);
        var level = points.Select(p => p.HeightPx - slope * p.Hours).Median();
        var scatter = points.Select(p => Math.Abs(p.HeightPx - slope * p.Hours - level)).Median() * 1.4826;
        return (level, scatter);
    }

    private static double ClampRisePercent(double value) =>
        Math.Clamp(value, 0, 500);

    private static double ClampPredictedPeak(double value) =>
        Math.Clamp(value, 0, 500);

    private bool SessionExpired(DateTimeOffset now) =>
        _samples.Count > 0 && (now - _sessionStart).TotalHours > _options.MaxSessionHours;

    private bool IsCollapseReset(double risePercent, double doughHeightPx)
    {
        // Absolute height drop below the session-start level: a re-feed empties the jar
        // below the baseline while the clamped rise percent hides it at 0% — and during a
        // lag phase the rise-median guard below never passes. Compare heights directly.
        if (_baselineDoughHeightPx is { } baseline
            && doughHeightPx < baseline * (1 - _options.ResetDropFraction))
        {
            return true;
        }
        if (_samples.Count < 5) return false;
        var recentMedian = _samples.TakeLast(5)
            .Select(s => s.RisePercent)
            .Median();
        return recentMedian > 20 && risePercent < recentMedian * (1 - _options.ResetDropFraction);
    }

    /// <summary>Rise rate (%/h), centred on the middle of the span it was fitted over (a trailing
    /// fit describes the rate about half a span ago, not now): the least-squares slope of the shortest trailing span whose
    /// standard error is within <see cref="AnalysisOptions.MaxRateStdErrPercentPerHour"/>, or
    /// null while no span within the look-back determines it. The error combines the sampling
    /// geometry (a longer or denser span pins the slope better) with the noise of a
    /// reading, taken as the larger of the configured floor and the scatter around the
    /// fit. A step or a handful of jittery frames therefore inflates the error and is
    /// withheld, rather than being clipped to a plausible-looking value.</summary>
    private SlopeSample? ComputeRate(DateTimeOffset now)
    {
        if (_baselineDoughHeightPx is not { } baseline || baseline <= 0) return null;
        var floorVariance = Math.Pow(_options.RateNoiseFloorPx / baseline * 100.0, 2);
        var cutoff = now.AddMinutes(-_options.SlopeWindowMinutes);
        // Running sums over the trailing samples, newest first, in hours relative to now.
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        for (var i = _samples.Count - 1; i >= 0 && _samples[i].Time >= cutoff; i--)
        {
            var x = (_samples[i].Time - now).TotalHours;
            var y = _samples[i].RisePercent;
            n++;
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
            if (n < MinRateSamples) continue;
            var spreadX = sxx - sx * sx / n;
            if (spreadX <= 0) continue;
            var covariance = sxy - sx * sy / n;
            var slope = covariance / spreadX;
            var residualSumSquares = Math.Max(0, syy - sy * sy / n - slope * covariance);
            var noiseVariance = Math.Max(floorVariance, residualSumSquares / (n - 2));
            if (Math.Sqrt(noiseVariance / spreadX) <= _options.MaxRateStdErrPercentPerHour)
                return new SlopeSample(now, slope, now.AddHours(sx / n));
        }
        return null;
    }

    private void UpdatePeakState()
    {
        var count = Math.Max(1, _options.PeakConfirmWindows);
        if (_slopes.Count < count) return;
        if (_peaked)
        {
            UpdateUnpeakState(count);
            return;
        }
        var maxRise = _samples.Max(s => s.RisePercent);
        if (maxRise < _options.MinRisePercentForPeak) return;
        // No peak without measured evidence that the dough really rose at some point.
        if (_maximumSlope < SlopeEvidence) return;
        // The rate itself is the criterion: a rise that has been flat or falling for the
        // whole confirming span is the peak. The rise so far is the reference for resumed growth.
        for (var i = _slopes.Count - count; i < _slopes.Count; i++)
            if (_slopes[i].Slope > _options.FlatSlopePercentPerHour) return;
        _peaked = true;
        _peakReferenceRisePercent = _samples.Max(s => s.RisePercent);
    }

    /// <summary>Revokes a peak only after confirmed positive slopes and current growth
    /// beyond the remembered peak plus measurement jitter; session history is retained.</summary>
    private void UpdateUnpeakState(int count)
    {
        for (var i = _slopes.Count - count; i < _slopes.Count; i++)
            if (_slopes[i].Slope <= _options.FlatSlopePercentPerHour) return;
        if (_peakReferenceRisePercent is not { } reference
            || _baselineDoughHeightPx is not { } baseline || baseline <= 0) return;
        var jitterMarginPercent = _options.JitterTolerancePx / baseline * 100.0;
        if (_samples[^1].RisePercent <= reference + jitterMarginPercent) return;
        _peaked = false;
    }

    /// <summary>During the first <see cref="AnalysisOptions.BaselineSettleMinutes"/> of a session the
    /// baseline is the median of its readings so far instead of the first one: a single reading
    /// is off by 10 px or more now and then (586 px against 598 px on 2026-10-14, 11 % of the start
    /// height), and that error scales every rise percentage of the session. Dough hardly rises in
    /// that time. Returns the new baseline when it changed.</summary>
    private double? SettleBaseline(LevelMeasurement m)
    {
        if (_startHeights.Count == 0 || _baselineDoughHeightPx is not { } baseline) return null;
        if ((m.Time - _sessionStart).TotalMinutes > _options.BaselineSettleMinutes)
        {
            _startHeights.Clear();
            return null;
        }
        _startHeights.Add(m.DoughHeightPx);
        var settled = _startHeights.Median();
        if (Math.Abs(settled - baseline) < 1e-9) return null;
        var factor = baseline / settled;
        double Rescale(double risePercent) => (risePercent + 100.0) * factor - 100.0;
        for (var i = 0; i < _samples.Count; i++)
            _samples[i] = _samples[i] with
            {
                RisePercent = Rescale(_samples[i].RisePercent),
                RawRisePercent = _samples[i].RawRisePercent is { } raw ? Rescale(raw) : null
            };
        for (var i = 0; i < _slopes.Count; i++)
            _slopes[i] = _slopes[i] with { Slope = _slopes[i].Slope * factor };
        _maximumSlope *= factor;
        _peakReferenceRisePercent = _peakReferenceRisePercent is { } reference ? Rescale(reference) : null;
        _baselineDoughHeightPx = settled;
        return settled;
    }

    /// <summary>Re-expresses the running session against the dough floor, which lies
    /// <paramref name="offsetPx"/> above the glass bottom the baseline was measured from. Heights
    /// shrink by the offset, so every stored rise percentage and slope scales by
    /// baseline / (baseline - offset). Returns the new baseline, or null when the offset is
    /// implausible (the session then restarts).</summary>
    private double? RebaseToDoughFloor(double offsetPx)
    {
        if (_baselineDoughHeightPx is not { } baseline) return null;
        var rebased = baseline - offsetPx;
        if (offsetPx < 0 || rebased < baseline * 0.5) return null;
        var scale = baseline / rebased;
        for (var i = 0; i < _samples.Count; i++)
            _samples[i] = _samples[i] with
            {
                RisePercent = _samples[i].RisePercent * scale,
                RawRisePercent = _samples[i].RawRisePercent * scale
            };
        for (var i = 0; i < _slopes.Count; i++)
            _slopes[i] = _slopes[i] with { Slope = _slopes[i].Slope * scale };
        _maximumSlope *= scale;
        _peakReferenceRisePercent *= scale;
        var window = _heightWindow.Select(height => height - offsetPx).ToArray();
        _heightWindow.Clear();
        foreach (var height in window) _heightWindow.Enqueue(height);
        _lastAcceptedHeightPx -= offsetPx;
        _pendingCollapseHeights?.Clear();
        _startHeights.Clear();
        _baselineDoughHeightPx = rebased;
        _baselineOnDoughFloor = true;
        return rebased;
    }

    private void ResetSession(DateTimeOffset start, double? baselinePx, bool onDoughFloor = false)
    {
        _samples.Clear();
        _slopes.Clear();
        _heightWindow.Clear();
        _baselineDoughHeightPx = baselinePx;
        _baselineOnDoughFloor = onDoughFloor && baselinePx is not null;
        _sessionStart = start;
        _peaked = false;
        _peakReferenceRisePercent = null;
        _pendingCollapseHeights?.Clear();
        _startHeights.Clear();
        _maximumSlope = 0;
        _lastAcceptedHeightPx = null;
        _lastMeasurementTime = null;
        _implausibleStreak = 0;
        _lastPublishedEta = null;
        _recentForecasts.Clear();
    }

    /// <summary>Stores the summary of the session being replaced (kept only for sessions with
    /// enough accepted samples to mean something) for <see cref="DequeueSessionSummary"/>.</summary>
    private void CaptureSessionSummary(DateTimeOffset sessionEnd)
    {
        if (_samples.Count < MinSamplesForSummary) return;
        var peak = _samples[0];
        foreach (var sample in _samples)
            if (sample.RisePercent > peak.RisePercent) peak = sample;
        var error = _lastPublishedEta is { } eta ? (eta - peak.Time).TotalMinutes : (double?)null;
        _pendingSummary = new SessionSummary(
            _sessionStart,
            sessionEnd,
            peak.RisePercent,
            peak.Time,
            _slopes.Count > 0 ? _maximumSlope : null,
            _lastPublishedEta,
            error);
    }

    /// <summary>Returns the summary of the session that ended since the last call, or null.</summary>
    public SessionSummary? DequeueSessionSummary()
    {
        var summary = _pendingSummary;
        _pendingSummary = null;
        return summary;
    }

    private RiseReading StartMeasuredSession(DateTimeOffset time, double heightPx, bool onDoughFloor)
    {
        CaptureSessionSummary(time);
        ResetSession(time, heightPx, onDoughFloor);
        _lastAcceptedHeightPx = heightPx;
        _startHeights.Add(heightPx);
        _lastMeasurementTime = time;
        SaveState();
        return new RiseReading(time, 0, null, null, null, null, null, false, NewSession: true, SessionStart: time);
    }

    private void SaveState()
    {
        if (string.IsNullOrWhiteSpace(_options.StateFilePath)) return;
        var state = new AnalyzerState(
            _samples.ToList(),
            _slopes.ToList(),
            _baselineDoughHeightPx,
            _sessionStart,
            _peaked,
            _peakReferenceRisePercent,
            _baselineOnDoughFloor);
        var path = ResolvePath(_options.StateFilePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(state, _jsonOptions));
    }

    private void RestoreState()
    {
        if (string.IsNullOrWhiteSpace(_options.StateFilePath)) return;
        var path = ResolvePath(_options.StateFilePath);
        if (!File.Exists(path)) return;
        try
        {
            var state = JsonSerializer.Deserialize<AnalyzerState>(File.ReadAllText(path), _jsonOptions);
            if (state is null) return;
            _samples.Clear();
            _samples.AddRange(state.Samples);
            _slopes.Clear();
            _slopes.AddRange(state.Slopes);
            _maximumSlope = 0;
            foreach (var slope in _slopes) _maximumSlope = Math.Max(_maximumSlope, slope.Slope);
            _baselineDoughHeightPx = state.BaselineDoughHeightPx;
            _baselineOnDoughFloor = state.BaselineOnDoughFloor;
            _sessionStart = state.SessionStart;
            _peaked = state.Peaked;
            _peakReferenceRisePercent = state.PeakReferenceRisePercent;
            if (_samples.Count > 0 && SessionExpired(DateTimeOffset.UtcNow))
            {
                ResetSession(DateTimeOffset.UtcNow, null);
            }
            else if (_samples.Count > 0 && _baselineDoughHeightPx is not null)
            {
                // Reconstruct the plausibility gate's reference point from the last persisted
                // sample so a restart doesn't leave the very next reading ungated.
                var last = _samples[^1];
                _lastAcceptedHeightPx = _baselineDoughHeightPx.Value * (1 + last.RisePercent / 100.0);
                _lastMeasurementTime = last.Time;
                // Older persisted peaks have no anchor. Require growth beyond their
                // observed maximum rather than guessing an earlier plateau.
                if (_peaked && _peakReferenceRisePercent is null)
                    _peakReferenceRisePercent = _samples.Max(s => s.RisePercent);
            }
        }
        catch (Exception)
        {
            ResetSession(DateTimeOffset.UtcNow, null);
        }
    }

    private static string ResolvePath(string path) =>
        Path.IsPathFullyQualified(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private sealed record AnalyzerState(
        List<Sample> Samples,
        List<SlopeSample> Slopes,
        double? BaselineDoughHeightPx,
        DateTimeOffset SessionStart,
        bool Peaked,
        double? PeakReferenceRisePercent = null,
        bool BaselineOnDoughFloor = false);
}
