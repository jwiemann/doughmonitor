using System.Text.Json;
using MathNet.Numerics;
using MathNet.Numerics.Statistics;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Analysis;

/// <summary>Stateful per-session analysis: baseline tracking, auto-reset, rolling slope, sigmoid-based ETA.</summary>
public sealed class RiseAnalyzer
{
    private readonly AnalysisOptions _options;
    private readonly List<Sample> _samples = [];
    private readonly List<(DateTimeOffset Time, double Slope)> _slopes = [];
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private double? _baselineDoughHeightPx;
    private DateTimeOffset _sessionStart;
    private bool _peaked;

    public RiseAnalyzer(AnalysisOptions options)
    {
        _options = options;
        RestoreState();
    }

    public void Reset()
    {
        ResetSession(DateTimeOffset.MinValue, null);
        SaveState();
    }

    public RiseReading Analyze(LevelMeasurement m)
    {
        if (_baselineDoughHeightPx is null || SessionExpired(m.Time))
        {
            ResetSession(m.Time, m.DoughHeightPx);
            SaveState();
            return new RiseReading(m.Time, 0, null, null, null, false, NewSession: true);
        }

        var risePercent = (m.DoughHeightPx - _baselineDoughHeightPx.Value) / _baselineDoughHeightPx.Value * 100.0;

        if (IsCollapseReset(risePercent))
        {
            ResetSession(m.Time, m.DoughHeightPx);
            SaveState();
            return new RiseReading(m.Time, 0, null, null, null, false, NewSession: true);
        }

        _samples.Add(new Sample(m.Time, risePercent));

        var slope = ComputeWindowSlope(m.Time);
        if (slope is not null)
        {
            _slopes.Add((m.Time, slope.Value));
            UpdatePeakState();
        }

        SigmoidFit? fit = null;
        if (!_peaked && _samples.Count >= _options.MinSamplesForFit)
        {
            fit = SigmoidFitter.TryFit(_samples);
            if (fit is not null && fit.RelativeStdError > _options.MaxEtaRelativeStdError)
                fit = null;
        }

        SaveState();

        return new RiseReading(
            m.Time,
            Math.Round(risePercent, 1),
            slope is null ? null : Math.Round(slope.Value, 1),
            fit is null ? null : Math.Round(fit.L, 0),
            fit is null ? null : _sessionStart.AddHours(fit.PeakHoursFromStart),
            _peaked,
            NewSession: false);
    }

    private bool SessionExpired(DateTimeOffset now) =>
        _samples.Count > 0 && (now - _sessionStart).TotalHours > _options.MaxSessionHours;

    private bool IsCollapseReset(double risePercent)
    {
        if (_samples.Count < 5) return false;
        var recentMedian = _samples.TakeLast(5).Select(s => s.RisePercent).Median();
        return recentMedian > 20 && risePercent < recentMedian * (1 - _options.ResetDropFraction);
    }

    private double? ComputeWindowSlope(DateTimeOffset now)
    {
        var window = _samples
            .Where(s => (now - s.Time).TotalMinutes <= _options.SlopeWindowMinutes)
            .ToArray();
        if (window.Length < 4) return null;

        var x = window.Select(s => (s.Time - window[0].Time).TotalHours).ToArray();
        var y = window.Select(s => s.RisePercent).ToArray();
        return Fit.Line(x, y).B;
    }

    private void UpdatePeakState()
    {
        if (_peaked || _slopes.Count < _options.PeakConfirmWindows) return;

        var maxRise = _samples.Max(s => s.RisePercent);
        var flatOrFalling = _slopes.TakeLast(_options.PeakConfirmWindows).All(s => s.Slope <= 0.5);
        _peaked = maxRise > 25 && flatOrFalling;
    }

    private void ResetSession(DateTimeOffset start, double? baselinePx)
    {
        _samples.Clear();
        _slopes.Clear();
        _baselineDoughHeightPx = baselinePx;
        _sessionStart = start;
        _peaked = false;
    }

    private void SaveState()
    {
        if (string.IsNullOrWhiteSpace(_options.StateFilePath)) return;

        var state = new AnalyzerState(_samples.ToList(), _slopes.ToList(), _baselineDoughHeightPx, _sessionStart, _peaked);
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
                ResetSession(DateTimeOffset.UtcNow, null);
        }
        catch (Exception)
        {
            ResetSession(DateTimeOffset.UtcNow, null);
        }
    }

    private static string ResolvePath(string path) =>
        Path.IsPathFullyQualified(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}

internal sealed record AnalyzerState(
    List<Sample> Samples,
    List<(DateTimeOffset Time, double Slope)> Slopes,
    double? BaselineDoughHeightPx,
    DateTimeOffset SessionStart,
    bool Peaked);
