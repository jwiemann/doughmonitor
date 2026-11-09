using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Services;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class ArchiveBackfillTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 14, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateTempSubdirectory("backfill").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    /// <summary>Answers per capture time: a dough height, or "empty_scene" for a removed jar.</summary>
    private sealed class FakeDetector(Dictionary<DateTimeOffset, double?> heights) : IJarDetector
    {
        public event Action? SceneChanged { add { } remove { } }
        public DetectionDiagnostics? LastDiagnostics => null;
        public string LastOutcome { get; private set; } = "";
        public byte[]? LatestAnnotatedImageBytes => null;

        public Task<LevelMeasurement?> MeasureAsync(
            byte[] jpegBytes, DateTimeOffset now, double? sessionBaselineHeightPx, CancellationToken ct)
        {
            var height = heights[now];
            LastOutcome = height is null ? "empty_scene" : "detected";
            return Task.FromResult(height is { } h ? new LevelMeasurement(now, 400 - h, 0, 400) : null);
        }
    }

    private void Frame(DateTimeOffset time) =>
        File.WriteAllBytes(Path.Combine(_directory, $"{time:yyyyMMdd_HHmmssfff}.jpg"), [1]);

    [Fact]
    public async Task FramesOfTheWindow_RebuildTheSessionFromTheJarSwap()
    {
        var heights = new Dictionary<DateTimeOffset, double?>();
        // Outside the window (more than 36 h old): must not be replayed at all.
        heights[Now.AddHours(-40)] = 150;
        // The old jar, then three empty frames (a swap), then the new jar rising slowly.
        for (var i = 0; i < 6; i++) heights[Now.AddHours(-3).AddMinutes(i * 5)] = 110 + i;
        for (var i = 0; i < 3; i++) heights[Now.AddHours(-2).AddMinutes(i * 5)] = null;
        for (var i = 0; i < 12; i++) heights[Now.AddHours(-2).AddMinutes(15 + i * 5)] = 220 + i;
        foreach (var time in heights.Keys) Frame(time);
        var analyzer = new RiseAnalyzer(new AnalysisOptions { StateFilePath = null, MedianWindowSize = 1 });

        var result = await ArchiveBackfill.RunAsync(
            _directory, new FakeDetector(heights), analyzer, Now, TimeSpan.FromHours(36), CancellationToken.None);

        Assert.Equal(heights.Count - 1, result.Frames);
        Assert.Equal(6 + 12, result.Measured);
        Assert.Equal("jar removed for 10 min", analyzer.LastSessionStartReason);
        // The session is the one that started at the swap, not the older jar's.
        Assert.InRange(analyzer.BaselineDoughHeightPx!.Value, 219, 222);
    }

    [Fact]
    public async Task MissingDirectory_ChangesNothing()
    {
        var analyzer = new RiseAnalyzer(new AnalysisOptions { StateFilePath = null });

        var result = await ArchiveBackfill.RunAsync(
            Path.Combine(_directory, "none"), new FakeDetector([]), analyzer, Now, TimeSpan.FromHours(36), CancellationToken.None);

        Assert.Equal(0, result.Frames);
        Assert.Null(analyzer.BaselineDoughHeightPx);
    }
}
