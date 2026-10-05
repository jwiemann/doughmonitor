using System.Text.Json;

using MathNet.Numerics;
using MathNet.Numerics.Statistics;

using SourdoughMonitor.Config;

namespace SourdoughMonitor.Analysis;

using Models;

/// <summary>Stateful per-session analysis: baseline tracking, auto-reset, rolling slope, sigmoid-based ETA.</summary>
public sealed class RiseAnalyzer
{
    private readonly AnalysisOptions _options;
    private readonly List<Sample> _samples = [];
    private readonly List<SlopeSample> _slopes = [];
    private readonly Queue<double> _heightWindow = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private double? _baselineDoughHeightPx;
    private DateTimeOffset _sessionStart;
    private bool _peaked;
    private SigmoidFit? _lastFit;
    private double? _lastAcceptedHeightPx;
    private DateTimeOffset? _lastMeasurementTime;
    private int _implausibleStreak;
    private int _collapseStreak;
    private double _maximumSlope;
    private double? _peakReferenceRisePercent;

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
        ResetSession(DateTimeOffset.MinValue, null);
        SaveState();
        return new RiseReading(DateTimeOffset.UtcNow, 0, null, null, null, false, NewSession: true);
    }

    public RiseReading? Analyze(LevelMeasurement m)
    {
        var hasActiveSession = _baselineDoughHeightPx is not null && !SessionExpired(m.Time);
        // Physical plausibility gate: reject a raw reading that implies the dough moved
        // faster than organic fermentation can (camera glitch, misdetected frame - e.g.
        // locking onto glare or the jar's own base) before it ever reaches the smoothing
        // window, rather than letting a single bad frame drag the median toward it.
        // But real dough handling - feeding the starter, punching down before shaping, a
        // fold that briefly puffs the dough up before it settles into a bigger container -
        // also moves the surface faster than this budget allows, and unlike a one-off
        // misdetected frame, it persists across samples instead of reverting on the next
        // one. Cap how many consecutive frames the gate can reject so a real handling event
        // is only briefly "unavailable" instead of locked out until enough elapsed time
        // inflates the budget past it; downstream, the existing collapse-reset logic
        // recognizes a genuine sustained drop and starts a fresh baseline for it.
        if (hasActiveSession && IsImplausibleJump(m.DoughHeightPx, m.Time))
        {
            _implausibleStreak++;
            if (_implausibleStreak <= _options.MaxImplausibleJumpRejects) return null;
            // Streak exhausted: a misdetected frame doesn't repeat identically this many
            // times in a row, so trust it as a real (if abrupt) change and stop rejecting.
        }
        _implausibleStreak = 0;

        // Smooth the raw per-frame pixel height first: a single noisy/condensation-affected
        // frame would otherwise propagate straight into the baseline and every downstream
        // percentage, slope and fit computed from it.
        var smoothedHeightPx = Smooth(m.DoughHeightPx);
        _lastAcceptedHeightPx = smoothedHeightPx;
        _lastMeasurementTime = m.Time;
        if (!hasActiveSession)
        {
            ResetSession(m.Time, smoothedHeightPx);
            SaveState();
            return new RiseReading(m.Time, 0, null, null, null, false, NewSession: true, SessionStart: _sessionStart);
        }
        var risePercent = (smoothedHeightPx - _baselineDoughHeightPx.Value) / _baselineDoughHeightPx.Value * 100.0;
        if (IsCollapseReset(risePercent, smoothedHeightPx))
        {
            // A single frame that looks like a collapse is exactly what a jar reappearing
            // after a detection gap (occlusion, glare while reacquiring) tends to produce:
            // the vision pipeline hasn't locked back onto the true surface yet. A genuine
            // collapse (punch-down, deflating starter) keeps reporting the lower level on
            // the next samples instead of reverting, so require it to persist for a few
            // consecutive samples before wiping the session; treat the unconfirmed ones as
            // unavailable rather than resetting on the first sighting.
            _collapseStreak++;
            if (_collapseStreak < _options.CollapseConfirmSamples) return null;
            ResetSession(m.Time, smoothedHeightPx);
            SaveState();
            return new RiseReading(m.Time, 0, null, null, null, false, NewSession: true, SessionStart: _sessionStart);
        }
        _collapseStreak = 0;
        _samples.Add(new Sample(m.Time, risePercent));
        var slope = ComputeWindowSlope(m.Time);
        if (slope is { } rate)
        {
            _slopes.Add(new SlopeSample(m.Time, rate));
            _maximumSlope = Math.Max(_maximumSlope, rate);
        }
        SigmoidFit? fit = null;
        if (!_peaked && _samples.Count >= _options.MinSamplesForFit)
        {
            fit = SigmoidFitter.TryFit(_samples, _lastFit);
            if (fit is not null && fit.RelativeStdError > _options.MaxEtaRelativeStdError)
                fit = null;
        }
        if (fit is not null) _lastFit = fit;
        if (fit is not null && !ForecastIsInformative(fit, m.Time)) fit = null;
        if (slope is not null) UpdatePeakState(fit);
        if (_peaked) fit = null;
        SaveState();
        return new RiseReading(
            m.Time,
            Math.Round(ClampRisePercent(risePercent), 1),
            slope is null ? null : Math.Round(ClampRiseRate(slope.Value), 1),
            fit is null ? null : Math.Round(ClampPredictedPeak(fit.L * _options.PeakFraction), 0),
            fit is null ? null : _sessionStart.AddHours(fit.HoursAtFraction(_options.PeakFraction)),
            _peaked,
            NewSession: false,
            SessionStart: _sessionStart);
    }

    private bool ForecastIsInformative(SigmoidFit fit, DateTimeOffset now)
    {
        var count = Math.Max(1, _options.PeakConfirmWindows);
        if (_slopes.Count < count + 1 || _samples[^1].RisePercent < _options.MinRisePercentForPeak)
            return false;
        if ((now - _sessionStart).TotalHours < fit.T0 || _maximumSlope <= 0
            || _slopes[^1].Slope > _maximumSlope * 0.95)
            return false;
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

    private static double ClampRiseRate(double value) =>
        Math.Clamp(value, -100, 500);

    private static double ClampPredictedPeak(double value) =>
        Math.Clamp(value, 0, 500);

    private bool SessionExpired(DateTimeOffset now) =>
        _samples.Count > 0 && (now - _sessionStart).TotalHours > _options.MaxSessionHours;

    private bool IsCollapseReset(double risePercent, double smoothedHeightPx)
    {
        if (_samples.Count < 5) return false;
        // Absolute height drop below the session-start level: a re-feed empties the jar
        // below the baseline while the clamped rise percent hides it at 0% — and during a
        // lag phase the rise-median guard below never passes. Compare heights directly.
        if (_baselineDoughHeightPx is { } baseline
            && smoothedHeightPx < baseline * (1 - _options.ResetDropFraction))
        {
            return true;
        }
        var recentMedian = _samples.TakeLast(5)
            .Select(s => s.RisePercent)
            .Median();
        return recentMedian > 20 && risePercent < recentMedian * (1 - _options.ResetDropFraction);
    }

    private double? ComputeWindowSlope(DateTimeOffset now)
    {
        var window = _samples.Where(s => (now - s.Time).TotalMinutes <= _options.SlopeWindowMinutes)
            .ToArray();
        if (window.Length < 4) return null;
        var x = window.Select(s => (s.Time - window[0].Time).TotalHours)
            .ToArray();
        var y = window.Select(s => s.RisePercent)
            .ToArray();
        return Fit.Line(x, y)
            .B;
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
        var flatOrFalling = true;
        for (var i = _slopes.Count - count; i < _slopes.Count; i++)
            if (_slopes[i].Slope > _options.FlatSlopePercentPerHour) flatOrFalling = false;
        // After resumed growth, a refit can still follow the old plateau. Require an
        // observed new flat/falling period before confirming another peak.
        var practicalPeak = _peakReferenceRisePercent is null && fit is not null && _samples.Count >= count;
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

    private void ResetSession(DateTimeOffset start, double? baselinePx)
    {
        _samples.Clear();
        _slopes.Clear();
        _heightWindow.Clear();
        _baselineDoughHeightPx = baselinePx;
        _sessionStart = start;
        _peaked = false;
        _lastFit = null;
        _peakReferenceRisePercent = null;
        _collapseStreak = 0;
        _maximumSlope = 0;
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
            _peakReferenceRisePercent);
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
        double? PeakReferenceRisePercent = null);
}
