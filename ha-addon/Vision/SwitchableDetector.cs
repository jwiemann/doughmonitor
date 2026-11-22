using SourdoughMonitor.Analysis;

namespace SourdoughMonitor.Vision;

/// <summary>Runs either the vision-LLM detector or the classic image-pattern detector, chosen at
/// runtime (Home Assistant switch). The choice is persisted so it survives a restart.
/// <para>Both feed one rise session: after a switch the new detector's jar bottom is replaced by the
/// bottom the session was measured with, and the classic detector's dough floor stands while the LLM
/// detector (which measures none) gets the last known floor offset. Without it the detectors'
/// slightly different rows would shift the dough height, and with it every rise percentage, at the
/// moment of the switch.</para></summary>
public sealed class SwitchableDetector : IJarDetector
{
    private readonly IJarDetector _classic;
    private readonly IJarDetector _llm;
    private readonly string? _stateFilePath;
    private volatile bool _llmEnabled;
    private IJarDetector _current;
    private IJarDetector? _lastSource;
    private double? _lastBottomPx;
    private double? _anchorBottomPx;
    private double? _floorOffsetPx;

    public SwitchableDetector(IJarDetector classic, IJarDetector llm, string? stateFilePath, bool llmDefault = true)
    {
        _classic = classic;
        _llm = llm;
        _stateFilePath = string.IsNullOrWhiteSpace(stateFilePath) ? null : stateFilePath;
        _llmEnabled = LoadState() ?? llmDefault;
        _current = _llmEnabled ? _llm : _classic;
        classic.SceneChanged += OnSceneChanged;
        llm.SceneChanged += OnSceneChanged;
    }

    public event Action? SceneChanged;

    /// <summary>True while measurements come from the vision LLM, false for the classic detector.</summary>
    public bool LlmEnabled
    {
        get => _llmEnabled;
        set
        {
            if (_llmEnabled == value) return;
            _llmEnabled = value;
            SaveState();
        }
    }

    public DetectionDiagnostics? LastDiagnostics => _current.LastDiagnostics;

    public string LastOutcome => _current.LastOutcome;

    public byte[]? LatestAnnotatedImageBytes => _current.LatestAnnotatedImageBytes;

    /// <summary>With the switch off, the classic detector measures and the vision LLM steps in only for a frame
    /// the classic one cannot place (no surface or jar found) or places implausibly (a jump the dough cannot
    /// make in one cycle, or a surface below the floor): the model then decides. Dark frames are not escalated,
    /// the model cannot see in the dark either. Off during the startup replay of the archive (cost).</summary>
    public bool LlmFallback { get; set; } = true;

    private double? _lastTopPx;
    private DateTimeOffset? _lastTime;

    /// <summary>A surface this far from the last one (share of the dough height, at least 25 px) within
    /// <see cref="PlausibleGap"/> is not trusted: the dough rises 2 % in five minutes at its fastest.</summary>
    private const double MaxJumpShare = 0.08;
    private const double MinJumpPx = 25;
    private static readonly TimeSpan PlausibleGap = TimeSpan.FromMinutes(30);

    private bool Implausible(LevelMeasurement m, DateTimeOffset now)
    {
        if (m.DoughHeightPx <= 0 || m.DoughTopPx < m.JarTopPx) return true;
        if (_lastTopPx is not { } last || _lastTime is not { } at || now - at > PlausibleGap) return false;
        return Math.Abs(m.DoughTopPx - last) > Math.Max(MinJumpPx, MaxJumpShare * m.DoughHeightPx);
    }

    public async Task<LevelMeasurement?> MeasureAsync(
        byte[] jpegBytes, DateTimeOffset now, double? sessionBaselineHeightPx, CancellationToken ct)
    {
        var detector = _llmEnabled ? _llm : _classic;
        _current = detector;
        var measurement = await detector.MeasureAsync(jpegBytes, now, sessionBaselineHeightPx, ct);
        if (!_llmEnabled && LlmFallback)
        {
            var outcome = _classic.LastOutcome;
            var unplaced = measurement is null && outcome is "no_surface" or "no_jar";
            if (unplaced || (measurement is not null && Implausible(measurement, now)))
            {
                try
                {
                    var second = await _llm.MeasureAsync(jpegBytes, now, sessionBaselineHeightPx, ct);
                    if (second is not null)
                    {
                        (detector, measurement, _current) = (_llm, second, _llm);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // No model (no key, budget, network): the classic result stands, as without the fallback.
                }
            }
        }
        if (measurement is null) return null;
        _lastTopPx = measurement.DoughTopPx;
        _lastTime = now;
        if (_lastSource is not null && !ReferenceEquals(_lastSource, detector))
        {
            // The held bottom stands only for the same scene: the startup replay of the archive can end on
            // frames of another jar (2026-11-12: starter jar 634 px held for the bread bowl at 696 px).
            if (_lastBottomPx is { } held && Math.Abs(measurement.JarBottomPx - held) > 0.06 * measurement.JarBottomPx)
            {
                _anchorBottomPx = null;
                _floorOffsetPx = null;
            }
            else _anchorBottomPx ??= _lastBottomPx;
        }
        if (measurement.DoughFloorPx is { } ownFloor) _floorOffsetPx = measurement.JarBottomPx - ownFloor;
        var bottom = _anchorBottomPx ?? measurement.JarBottomPx;
        // The classic detector's floor stands; the LLM has none, so it gets the last known floor
        // (glass bottom less the held offset) and both feed one floor-based session.
        measurement = measurement with
        {
            DoughFloorPx = measurement.DoughFloorPx ?? (_floorOffsetPx is { } offset ? bottom - offset : null),
            JarBottomPx = bottom
        };
        _lastSource = detector;
        _lastBottomPx = measurement.JarBottomPx;
        return measurement;
    }

    /// <summary>A camera move invalidates the held bottom: the next session measures its own.</summary>
    private void OnSceneChanged()
    {
        _anchorBottomPx = null;
        _floorOffsetPx = null;
        _lastSource = null;
        _lastTopPx = null;
        _lastTime = null;
        _lastBottomPx = null;
        SceneChanged?.Invoke();
    }

    private bool? LoadState()
    {
        if (_stateFilePath is null) return null;
        try
        {
            return File.Exists(_stateFilePath)
                ? File.ReadAllText(_stateFilePath).Trim().ToLowerInvariant() switch
                {
                    "llm" => true,
                    "classic" => false,
                    _ => null
                }
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveState()
    {
        if (_stateFilePath is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_stateFilePath, _llmEnabled ? "llm" : "classic");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The choice still applies until restart; only its persistence failed.
        }
    }
}
