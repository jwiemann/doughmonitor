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
    private SigmoidFit? _lastFit;
    private double? _lastAcceptedHeightPx;
    private DateTimeOffset? _lastMeasurementTime;
    private int _implausibleStreak;
    private List<double>? _pendingCollapseHeights;
    private double _maximumSlope;
    private double? _peakReferenceRisePercent;
    private readonly List<(DateTimeOffset Time, double EtaHours)> _etaHistory = [];
    private DateTimeOffset? _lastPublishedEta;
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
        var smoothedHeightPx = Smooth(m.DoughHeightPx);
        _lastAcceptedHeightPx = smoothedHeightPx;
        _lastMeasurementTime = m.Time;
        var risePercent = (smoothedHeightPx - baseline) / baseline * 100.0;
        _samples.Add(new Sample(m.Time, risePercent));
        var slope = ComputeRate(m.Time);
        if (slope is { } rate)
        {
            _slopes.Add(new SlopeSample(m.Time, rate));
            _maximumSlope = Math.Max(_maximumSlope, rate);
        }
        SigmoidFitResult? fitResult = null;
        if (!_peaked && _samples.Count >= _options.MinSamplesForFit)
        {
            fitResult = SigmoidFitter.TryFit(_samples, _lastFit);
            if (fitResult is not null && fitResult.Best.RelativeStdError > _options.MaxEtaRelativeStdError)
                fitResult = null;
        }
        var fit = fitResult?.Best;
        if (fit is not null) _lastFit = fit;
        if (fit is not null && !ForecastIsInformative(fit, m.Time)) fit = null;
        if (slope is not null) UpdatePeakState(fit);
        if (_peaked) fit = null;
        DateTimeOffset? eta = null, etaLow = null, etaHigh = null;
        if (fit is not null && fitResult is not null)
            (eta, etaLow, etaHigh) = TrackEta(m.Time, fit, fitResult.NearOptimal);
        SaveState();
        return new RiseReading(
            m.Time,
            Math.Round(ClampRisePercent(risePercent), 1),
            slope is null ? null : Math.Round(slope.Value, 1),
            fit is null ? null : Math.Round(ClampPredictedPeak(fit.L * _options.PeakFraction), 0),
            eta,
            etaLow,
            etaHigh,
            _peaked,
            NewSession: false,
            SessionStart: _sessionStart);
    }

    /// <summary>Records the accepted fit's peak ETA and returns the point ETA (only while the
    /// recent estimates agree within <see cref="AnalysisOptions.MaxEtaSpreadMinutes"/>) plus
    /// the low/high range over the recent estimates and the near-optimal fits.</summary>
    private (DateTimeOffset? Point, DateTimeOffset Low, DateTimeOffset High) TrackEta(
        DateTimeOffset now, SigmoidFit fit, IReadOnlyList<SigmoidFit> nearOptimal)
    {
        var cutoff = now.AddMinutes(-_options.EtaStabilityWindowMinutes);
        _etaHistory.RemoveAll(e => e.Time < cutoff);
        _etaHistory.Add((now, fit.HoursAtFraction(_options.PeakFraction)));
        double windowMin = double.MaxValue, windowMax = double.MinValue;
        foreach (var (_, hours) in _etaHistory)
        {
            windowMin = Math.Min(windowMin, hours);
            windowMax = Math.Max(windowMax, hours);
        }
        double low = windowMin, high = windowMax;
        foreach (var near in nearOptimal)
        {
            var hours = near.HoursAtFraction(_options.PeakFraction);
            low = Math.Min(low, hours);
            high = Math.Max(high, hours);
        }
        DateTimeOffset? point = null;
        if ((windowMax - windowMin) * 60.0 <= _options.MaxEtaSpreadMinutes)
        {
            point = _sessionStart.AddHours(_etaHistory[^1].EtaHours);
            _lastPublishedEta = point;
        }
        return (point, _sessionStart.AddHours(low), _sessionStart.AddHours(high));
    }

    private double SlopeEvidence => _options.MinSlopeEvidenceSigmas * _options.MaxRateStdErrPercentPerHour;

    /// <summary>How far below the session's maximum rate the current rate must sit for a
    /// measured turn: the statistical margin (rate error), but at least two fifths of the
    /// maximum rate — a brief dip during a steep spurt (observed: 17 %/h wobble under a
    /// 23.5 %/h spurt, mid-acceleration) is not a turn.</summary>
    private const double TurnFractionOfMaxSlope = 0.4;

    private double TurnMargin(double evidence) =>
        Math.Max(evidence, TurnFractionOfMaxSlope * _maximumSlope);

    private bool ForecastIsInformative(SigmoidFit fit, DateTimeOffset now)
    {
        var count = Math.Max(1, _options.PeakConfirmWindows);
        if (_slopes.Count < count + 1 || _samples[^1].RisePercent < _options.MinRisePercentForPeak)
            return false;
        if ((now - _sessionStart).TotalHours < fit.T0 || _maximumSlope <= 0)
            return false;
        // The maximum rate must prove a real rise happened, and the current rate must sit
        // measurably below it: while the rate is still at its maximum (accelerating or
        // plateauing at the top), the curve does not identify a peak.
        var evidence = SlopeEvidence;
        if (_maximumSlope < evidence || _slopes[^1].Slope > _maximumSlope - TurnMargin(evidence)) return false;
        // An accelerating segment does not identify a maximum. Require a sustained,
        // positive but declining rise rate before exposing the extrapolation.
        for (var i = _slopes.Count - count; i < _slopes.Count; i++)
        {
            if (_slopes[i].Slope <= 0 || _slopes[i].Slope > _slopes[i - 1].Slope) return false;
        }
        return true;
    }

    private double Smooth(double rawHeightPx)
    {
        _heightWindow.Enqueue(rawHeightPx);
        while (_heightWindow.Count > _options.MedianWindowSize)
            _heightWindow.Dequeue();
        return _heightWindow.Median();
    }

    private bool IsImplausibleJump(double rawHeightPx, DateTimeOffset now)
    {
        if (_lastAcceptedHeightPx is null || _lastMeasurementTime is null) return false;
        var minutes = (now - _lastMeasurementTime.Value).TotalMinutes;
        if (minutes <= 0) return true; // non-advancing or out-of-order timestamp
        var maxDelta = _options.MaxRisePxPerMinute * minutes + _options.JitterTolerancePx;
        return Math.Abs(rawHeightPx - _lastAcceptedHeightPx.Value) > maxDelta;
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

    /// <summary>Rise rate (%/h): the least-squares slope of the shortest trailing span whose
    /// standard error is within <see cref="AnalysisOptions.MaxRateStdErrPercentPerHour"/>, or
    /// null while no span within the look-back determines it. The error combines the sampling
    /// geometry (a longer or denser span pins the slope better) with the noise of a
    /// reading, taken as the larger of the configured floor and the scatter around the
    /// fit. A step or a handful of jittery frames therefore inflates the error and is
    /// withheld, rather than being clipped to a plausible-looking value.</summary>
    private double? ComputeRate(DateTimeOffset now)
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
                return slope;
        }
        return null;
    }

    private void UpdatePeakState(SigmoidFit? fit)
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
        var evidence = SlopeEvidence;
        if (_maximumSlope < evidence) return;
        var flatOrFalling = true;
        for (var i = _slopes.Count - count; i < _slopes.Count; i++)
            if (_slopes[i].Slope > _options.FlatSlopePercentPerHour) flatOrFalling = false;
        // After resumed growth, a refit can still follow the old plateau. Require an
        // observed new flat/falling period before confirming another peak. A fit that
        // merely reaches the current rise is not a peak while the rate is still near its
        // peak rate (accelerating): the rate must have measurably turned, and the turn
        // must hold across the confirming windows — a wobble in a noisy rate is not a turn.
        var practicalPeak = _peakReferenceRisePercent is null && fit is not null && _samples.Count >= count
            && _slopes[^1].Slope <= _maximumSlope - TurnMargin(evidence);
        if (practicalPeak)
        {
            for (var i = Math.Max(1, _slopes.Count - count); i < _slopes.Count; i++)
                if (_slopes[i].Slope <= 0 || _slopes[i].Slope > _slopes[i - 1].Slope) practicalPeak = false;
        }
        if (practicalPeak && fit is not null)
        {
            for (var i = _samples.Count - count; i < _samples.Count; i++)
                if (_samples[i].RisePercent < fit.L * _options.PeakFraction) practicalPeak = false;
        }
        if (!(flatOrFalling || practicalPeak)) return;
        _peaked = true;
        // A fitted peak uses the asymptote; a flat peak uses the observed maximum.
        // Normal asymptotic creep must not revoke a confirmed practical peak.
        _peakReferenceRisePercent = fit?.L ?? maxRise;
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
        // Refit the retained session without the invalid plateau's optimizer seed.
        _lastFit = null;
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
            _samples[i] = _samples[i] with { RisePercent = _samples[i].RisePercent * scale };
        for (var i = 0; i < _slopes.Count; i++)
            _slopes[i] = _slopes[i] with { Slope = _slopes[i].Slope * scale };
        _maximumSlope *= scale;
        _peakReferenceRisePercent *= scale;
        var window = _heightWindow.Select(height => height - offsetPx).ToArray();
        _heightWindow.Clear();
        foreach (var height in window) _heightWindow.Enqueue(height);
        _lastAcceptedHeightPx -= offsetPx;
        _pendingCollapseHeights?.Clear();
        _lastFit = null;
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
        _lastFit = null;
        _peakReferenceRisePercent = null;
        _pendingCollapseHeights?.Clear();
        _maximumSlope = 0;
        _lastAcceptedHeightPx = null;
        _lastMeasurementTime = null;
        _implausibleStreak = 0;
        _etaHistory.Clear();
        _lastPublishedEta = null;
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
