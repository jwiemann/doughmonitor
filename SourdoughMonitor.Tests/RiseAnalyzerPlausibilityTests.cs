using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Tests;

/// <summary>Domain gate: fed dough follows a smooth curve, so a reading far off the recent trend
/// is a misdetection and never reaches the rise, rate or forecast.</summary>
public class RiseAnalyzerPlausibilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Baseline height 100 px (jar bottom 400, surface 300), rising 24 px per hour: 24 %/h.</summary>
    private static LevelMeasurement Steady(int step, double offsetPx = 0)
    {
        var minutes = step * 5;
        return new LevelMeasurement(Start.AddMinutes(minutes), 300 - 24.0 * minutes / 60 + offsetPx, 0, 400);
    }

    private static RiseAnalyzer NewAnalyzer() => new(new AnalysisOptions { StateFilePath = null, MedianWindowSize = 1 });

    [Fact]
    public void SingleMisdetectedFrame_IsRejectedAndLeavesTheCurveUntouched()
    {
        var withSpike = NewAnalyzer();
        var clean = NewAnalyzer();
        RiseReading? afterSpike = null;
        for (var step = 0; step <= 24; step++)
        {
            clean.Analyze(Steady(step));
            var reading = withSpike.Analyze(step == 18 ? Steady(step, -20) : Steady(step));
            if (step == 18) Assert.Null(reading); // 20 px above a smooth 24 %/h trend
            if (step == 19) afterSpike = reading;
        }
        Assert.NotNull(afterSpike);
        Assert.Equal(clean.Analyze(Steady(25))!.RisePercent, withSpike.Analyze(Steady(25))!.RisePercent, 1);
    }

    [Fact]
    public void ChangeSmallerThanTheReadingNoise_IsAccepted()
    {
        var analyzer = NewAnalyzer();
        for (var step = 0; step <= 18; step++) analyzer.Analyze(Steady(step));
        Assert.NotNull(analyzer.Analyze(Steady(19, 6)));
    }

    [Fact]
    public void LevelThatStaysAway_IsAcceptedAfterTheRejectionCap()
    {
        // A misdetection reverts on the next frame; a level that persists is real (handling).
        var analyzer = NewAnalyzer();
        for (var step = 0; step <= 18; step++) analyzer.Analyze(Steady(step));
        Assert.Null(analyzer.Analyze(Steady(19, -25)));
        Assert.Null(analyzer.Analyze(Steady(20, -25)));
        Assert.NotNull(analyzer.Analyze(Steady(21, -25)));
    }

    [Fact]
    public void BaselineIsTheMedianOfTheFirstReadings_NotTheFirstReadingAlone()
    {
        // Session start reads 112 px, the dough really stands at 123 px and stays there: the
        // start reading must not scale the rise of the whole session by 11 %.
        var analyzer = NewAnalyzer();
        double[] heights = [112, 123, 123, 123, 123, 123, 123];
        RiseReading? last = null;
        for (var step = 0; step < heights.Length; step++)
            last = analyzer.Analyze(new LevelMeasurement(Start.AddMinutes(5 * step), 400 - heights[step], 0, 400));
        Assert.Equal(123, analyzer.BaselineDoughHeightPx!.Value, 6);
        Assert.InRange(last!.RisePercent, 0, 0.1);

        // A level that has really risen 20 % by the end is reported as 20 %, not 31 %.
        var rising = NewAnalyzer();
        foreach (var (minute, height) in new[] { (0, 112.0), (5, 123), (10, 123), (15, 123), (20, 123), (40, 135), (60, 147.6) })
            last = rising.Analyze(new LevelMeasurement(Start.AddMinutes(minute), 400 - height, 0, 400));
        Assert.InRange(last!.RisePercent, 19.5, 20.5);
    }
}
