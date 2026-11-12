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

    /// <summary>Surface row 300 falling by 0.4 px per step (a slow rise), against a basis that the
    /// detector re-estimates from 400 to 430 px after step 12.</summary>
    private static LevelMeasurement OnBasis(int step, double basisPx) =>
        new(Start.AddMinutes(step * 5), 300 - 0.4 * step, 0, basisPx);

    [Fact]
    public void BasisReEstimatedAtAConstantSurface_IsNotCountedAsRise()
    {
        var shifted = NewAnalyzer();
        var steady = NewAnalyzer();
        RiseReading? last = null, reference = null;
        for (var step = 0; step < 30; step++)
        {
            reference = steady.Analyze(OnBasis(step, 430));   // the basis the shifted run only learns later
            last = shifted.Analyze(OnBasis(step, step < 12 ? 400 : 430));
        }

        Assert.NotNull(last);
        // Without the rebase the 30 px step reads about +30 %; on the true basis the dough rose 11.6 px of 130.
        Assert.InRange(last!.RisePercent, 7, 11);
    }

    [Fact]
    public void SurfaceMovingWithTheBasis_IsNotRebased()
    {
        // The whole jar moved 30 px down (camera bump): the surface follows, so the height is unchanged.
        var analyzer = NewAnalyzer();
        for (var step = 0; step < 12; step++) analyzer.Analyze(OnBasis(step, 400));
        var moved = analyzer.Analyze(new LevelMeasurement(Start.AddMinutes(12 * 5), 300 - 0.4 * 12 + 30, 0, 430));

        Assert.NotNull(moved);
        Assert.InRange(moved!.RisePercent, 0, 5);
    }

    [Fact]
    public void FloorBasedSession_KeepsReadingOnFramesWithoutAFloor()
    {
        // The floor is measured now and then (25 px above the glass bottom); in between the frame
        // carries none. Those frames must not turn the whole session unavailable.
        var analyzer = NewAnalyzer();
        RiseReading? last = null;
        for (var step = 0; step < 20; step++)
        {
            double? floor = step % 6 == 0 ? 375 : null;
            last = analyzer.Analyze(new LevelMeasurement(Start.AddMinutes(step * 5), 300 - step, 0, 400, floor)) ?? last;
        }

        var unfloored = analyzer.Analyze(new LevelMeasurement(Start.AddMinutes(20 * 5), 300 - 20, 0, 400));

        Assert.NotNull(unfloored);
        Assert.InRange(unfloored!.RisePercent, 15, 40);
    }
}
