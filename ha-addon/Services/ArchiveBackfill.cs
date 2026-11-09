using SourdoughMonitor.Analysis;
using SourdoughMonitor.Replay;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Services;

/// <summary>Rebuilds the running rise session from the snapshot archive: the frames of the last
/// <paramref name="window"/> go through the detector and the analyzer exactly as live frames
/// would, in order and with their own capture time. A restart (an add-on update, say) therefore
/// does not lose the session as long as the archive holds its frames. Nothing is published from
/// here; the caller publishes the resulting state once.</summary>
public static class ArchiveBackfill
{
    public sealed record Result(int Frames, int Measured, RiseReading? LastReading);

    public static async Task<Result> RunAsync(
        string directory,
        IJarDetector detector,
        RiseAnalyzer analyzer,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return new Result(0, 0, null);
        var frames = new List<(string Path, DateTimeOffset Time)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.jpg"))
        {
            if (SnapshotTimestampResolver.TryParse(path, out var time) && time >= now - window && time <= now)
                frames.Add((path, time));
        }
        frames.Sort((a, b) => a.Time.CompareTo(b.Time));

        var measured = 0;
        RiseReading? last = null;
        foreach (var (path, time) in frames)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var measurement = await detector.MeasureAsync(bytes, time, analyzer.BaselineDoughHeightPx, ct);
            if (detector.LastOutcome == "empty_scene") analyzer.NoteEmptyScene(time);
            if (measurement is null) continue;
            measured++;
            if (analyzer.Analyze(measurement) is { } reading) last = reading;
        }
        // Sessions that ended during the backfill ended in the past: their summaries are not news.
        _ = analyzer.DequeueSessionSummary();
        return new Result(frames.Count, measured, last);
    }
}
