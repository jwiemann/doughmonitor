using SourdoughMonitor.Analysis;

namespace SourdoughMonitor.Vision;

/// <summary>Runs either the vision-LLM detector or the classic image-pattern detector, chosen at
/// runtime (Home Assistant switch). The choice is persisted so it survives a restart.
/// <para>Both feed one rise session, so their measurements are put on one height basis: the
/// glass bottom (the classic detector's dough floor is dropped, the LLM detector has none), and
/// after a switch the new detector's jar bottom is replaced by the bottom the session was
/// measured with. Without it the two detectors' slightly different bottom rows would shift the
/// dough height, and with it every rise percentage, at the moment of the switch.</para></summary>
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

    public async Task<LevelMeasurement?> MeasureAsync(
        byte[] jpegBytes, DateTimeOffset now, double? sessionBaselineHeightPx, CancellationToken ct)
    {
        var detector = _llmEnabled ? _llm : _classic;
        _current = detector;
        var measurement = await detector.MeasureAsync(jpegBytes, now, sessionBaselineHeightPx, ct);
        if (measurement is null) return null;
        if (_lastSource is not null && !ReferenceEquals(_lastSource, detector))
            _anchorBottomPx ??= _lastBottomPx;
        measurement = measurement with
        {
            DoughFloorPx = null,
            JarBottomPx = _anchorBottomPx ?? measurement.JarBottomPx
        };
        _lastSource = detector;
        _lastBottomPx = measurement.JarBottomPx;
        return measurement;
    }

    /// <summary>A camera move invalidates the held bottom: the next session measures its own.</summary>
    private void OnSceneChanged()
    {
        _anchorBottomPx = null;
        _lastSource = null;
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
