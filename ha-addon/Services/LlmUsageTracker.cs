using System.Text.Json;

namespace SourdoughMonitor.Services;

public sealed record LlmUsageSnapshot(
    DateOnly Day, int CallsToday, double CostTodayUsd, long CallsTotal, double CostTotalUsd);

/// <summary>Billed LLM calls and cost per UTC day and in total, persisted to a small JSON file so
/// the totals survive restarts. Only calls that were really sent (not cached replay answers) are
/// recorded. A null or empty path keeps the numbers in memory only.</summary>
public sealed class LlmUsageTracker
{
    private readonly object _gate = new();

    private readonly string? _path;

    private State _state;

    public LlmUsageTracker(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.IsPathFullyQualified(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        _state = Load(_path);
    }

    public void Record(int calls, double costUsd, DateOnly today)
    {
        if (calls == 0 && costUsd == 0) return;
        lock (_gate)
        {
            Rollover(today);
            _state = _state with
            {
                CallsToday = _state.CallsToday + calls,
                CostToday = _state.CostToday + costUsd,
                CallsTotal = _state.CallsTotal + calls,
                CostTotal = _state.CostTotal + costUsd
            };
            Save();
        }
    }

    public LlmUsageSnapshot Snapshot(DateOnly today)
    {
        lock (_gate)
        {
            Rollover(today);
            return new LlmUsageSnapshot(
                _state.Day, _state.CallsToday, _state.CostToday, _state.CallsTotal, _state.CostTotal);
        }
    }

    private void Rollover(DateOnly today)
    {
        if (_state.Day == today) return;
        _state = _state with { Day = today, CallsToday = 0, CostToday = 0 };
        Save();
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(_state));
        }
        catch (IOException)
        {
            // A failed write only loses persistence of the totals, never the measurement.
        }
    }

    private static State Load(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<State>(File.ReadAllText(path)) is { } state) return state;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
            }
        }
        return new State(DateOnly.FromDateTime(DateTime.UtcNow), 0, 0, 0, 0);
    }

    private sealed record State(DateOnly Day, int CallsToday, double CostToday, long CallsTotal, double CostTotal);
}
