using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Tests;

/// <summary>Session-start rules checked against the 2026-10 / 2026-11 archive replay: a small wobble
/// is not a feeding and a lost surface is not dough.</summary>
public class RiseAnalyzerSessionStartTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);

    private static LevelMeasurement Height(int step, double heightPx) =>
        new(Start.AddMinutes(step * 5), 400 - heightPx, 0, 400);

    private static RiseAnalyzer NewAnalyzer() => new(new AnalysisOptions { StateFilePath = null, MedianWindowSize = 1 });

    [Fact]
    public void RiseSlippingFromThirtyToTwentyOnePercent_DoesNotRestartTheSession()
    {
        var analyzer = NewAnalyzer();
        var heights = new List<double>();
        for (var h = 100.0; h <= 130; h += 2) heights.Add(h);       // rising to +30 %
        heights.AddRange(Enumerable.Repeat(130.0, 6));               // plateau
        heights.AddRange(Enumerable.Repeat(121.0, 8));               // level slips to +21 % and stays

        var restarts = 0;
        for (var step = 0; step < heights.Count; step++)
            if (analyzer.Analyze(Height(step, heights[step])) is { NewSession: true } && step > 0) restarts++;

        Assert.Equal(0, restarts);
    }

    [Fact]
    public void HeightOfALostSurface_IsUnavailableAndStartsNoSession()
    {
        var analyzer = NewAnalyzer();

        Assert.Null(analyzer.Analyze(Height(0, 11)));
        Assert.Null(analyzer.Analyze(Height(1, 3)));
        var first = analyzer.Analyze(Height(2, 110));

        Assert.NotNull(first);
        Assert.True(first!.NewSession);
    }

    private static RiseAnalyzer RunningSession(int steps = 12)
    {
        var analyzer = NewAnalyzer();
        for (var step = 0; step < steps; step++) analyzer.Analyze(Height(step, 100 + step));
        return analyzer;
    }

    [Fact]
    public void JarMissingForTenMinutes_StartsANewSessionWhenItReturns()
    {
        var analyzer = RunningSession();
        analyzer.NoteEmptyScene(Start.AddMinutes(12 * 5));
        analyzer.NoteEmptyScene(Start.AddMinutes(13 * 5));
        analyzer.NoteEmptyScene(Start.AddMinutes(14 * 5));

        var back = analyzer.Analyze(Height(15, 223));

        Assert.NotNull(back);
        Assert.True(back!.NewSession);
        Assert.Equal("jar removed for 10 min", analyzer.LastSessionStartReason);
    }

    [Fact]
    public void ShortAbsence_OrOneEndedByAMeasurement_KeepsTheSession()
    {
        var analyzer = RunningSession();
        analyzer.NoteEmptyScene(Start.AddMinutes(12 * 5));
        analyzer.NoteEmptyScene(Start.AddMinutes(13 * 5));           // five minutes only
        Assert.False(analyzer.Analyze(Height(14, 112))!.NewSession);

        analyzer.NoteEmptyScene(Start.AddMinutes(15 * 5));           // a measurement ended the earlier absence
        analyzer.NoteEmptyScene(Start.AddMinutes(16 * 5));
        Assert.False(analyzer.Analyze(Height(17, 115))!.NewSession);
    }

    [Fact]
    public void EmptyFramesScatteredOverHours_AreNotOneAbsence()
    {
        var analyzer = RunningSession();
        analyzer.NoteEmptyScene(Start.AddMinutes(12 * 5));
        analyzer.NoteEmptyScene(Start.AddMinutes(12 * 5 + 11 * 60));  // next morning's flicker, 11 h later

        Assert.False(analyzer.Analyze(Height(12 * 12 + 11 * 12 + 1, 112))!.NewSession);
    }
}
