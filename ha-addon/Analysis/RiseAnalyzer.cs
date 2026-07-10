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

    public RiseAnalyzer(AnalysisOptions options)
    {
        _options = options;
        RestoreState();
    }

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
            return new RiseReading(m.Time, 0, null, null, null, false, NewSession: true);
        }
        var risePercent = (smoothedHeightPx - _baselineDoughHeightPx.Value) / _baselineDoughHeightPx.Value * 100.0;
        if (IsCollapseReset(risePercent))
        {
            ResetSession(m.Time, smoothedHeightPx);
            SaveState();
            return new RiseReading(m.Time, 0, null, null, null, false, NewSession: true);
        }
        _samples.Add(new Sample(m.Time, risePercent));
        var slope = ComputeWindowSlope(m.Time);
        if (slope is not null)
            _slopes.Add(new SlopeSample(m.Time, slope.Value));
        SigmoidFit? fit = null;
        if (!_peaked && _samples.Count >= _options.MinSamplesForFit)
        {
            fit = SigmoidFitter.TryFit(_samples, _lastFit);
            if (fit is not null && fit.RelativeStdError > _options.MaxEtaRelativeStdError)
                fit = null;
        }
        if (fit is not null) _lastFit = fit;
        if (slope is not null) UpdatePeakState(fit);
        SaveState();
        return new RiseReading(
            m.Time,
            Math.Round(ClampRisePercent(risePercent), 1),
            slope is null ? null : Math.Round(ClampRiseRate(slope.Value), 1),
            fit is null ? null : Math.Round(ClampPredictedPeak(fit.L * _options.PeakFraction), 0),
            fit is null ? null : _sessionStart.AddHours(fit.HoursAtFraction(_options.PeakFraction)),
            _peaked,
            NewSession: false);
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

    private bool IsCollapseReset(double risePercent)
    {
        if (_samples.Count < 5) return false;
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
        if (_peaked || _slopes.Count < _options.PeakConfirmWindows) return;
        var flatOrFalling = _slopes.TakeLast(_options.PeakConfirmWindows)
            .All(s => s.Slope <= _options.FlatSlopePercentPerHour);
        if (!flatOrFalling) return;
        var maxRise = _samples.Max(s => s.RisePercent);
        // Prefer the fitted plateau (adapts to how far this particular starter actually
        // rises); fall back to a flat minimum-rise gate only while no fit is available yet,
        // so lag-phase noise is never mistaken for a peak.
        var reachedFittedPlateau = fit is not null && maxRise >= fit.L * _options.PeakFraction;
        _peaked = reachedFittedPlateau || maxRise >= _options.MinRisePercentForPeak;
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
    }

    private void SaveState()
    {
        if (string.IsNullOrWhiteSpace(_options.StateFilePath)) return;
        var state = new AnalyzerState(
            _samples.ToList(),
            _slopes.ToList(),
            _baselineDoughHeightPx,
            _sessionStart,
            _peaked);
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
            _baselineDoughHeightPx = state.BaselineDoughHeightPx;
            _sessionStart = state.SessionStart;
            _peaked = state.Peaked;
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
        bool Peaked);
}
