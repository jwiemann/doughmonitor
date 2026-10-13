using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Tests;

public class RiseAnalyzerForecastTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static LevelMeasurement KnownGrowth(int step)
    {
        var hours = step / 6.0;
        var rise = 100 / (1 + Math.Exp(-0.8 * (hours - 6)));
        return new LevelMeasurement(Start.AddHours(hours), 300 - rise, 0, 400);
    }

    [Fact]
    public void AcceleratingGrowth_DoesNotInventMaximumOrReadyTime()
    {
        var analyzer = new RiseAnalyzer(new AnalysisOptions { StateFilePath = null });
        for (var step = 0; step <= 30; step++)
        {
            var reading = analyzer.Analyze(KnownGrowth(step));
            Assert.NotNull(reading);
            Assert.Null(reading!.PredictedPeakPercent);
            Assert.Null(reading.PredictedPeakTime);
            Assert.False(reading.Peaked);
        }
    }

    /// <summary>The shape measured on 2026-10-13: the rate ramps up for 6 h to 20 %/h, then
    /// falls linearly to zero within 1.7 h — the rise stops abruptly, not asymptotically.</summary>
    private static LevelMeasurement RampThenFall(int step)
    {
        const double up = 6, down = 1.7, peakRate = 20;
        var hours = step / 12.0;
        var tau = Math.Clamp(hours - up, 0, down);
        var rise = hours <= up
            ? peakRate * hours * hours / (2 * up)
            : peakRate * up / 2 + peakRate * (tau - tau * tau / (2 * down));
        return new LevelMeasurement(Start.AddHours(hours), 300 - 100 * (1 + rise / 100.0) + 100, 0, 400);
    }

    [Fact]
    public void RateFallingToZero_PredictsPeakTimeAndHeightBeforeItArrivesThenConfirmsIt()
    {
        var analyzer = new RiseAnalyzer(new AnalysisOptions { StateFilePath = null });
        RiseReading? beforePeak = null;
        RiseReading? last = null;
        for (var step = 0; step <= 12 * 10; step++)
        {
            last = analyzer.Analyze(RampThenFall(step));
            if (step == 12 * 7 + 8) beforePeak = last; // 7.67 h: 3 min of the 1.7 h fall are left
        }
        Assert.NotNull(beforePeak?.PredictedPeakTime);
        var predictedHours = (beforePeak!.PredictedPeakTime!.Value - Start).TotalHours;
        Assert.InRange(predictedHours, 7.7 - 0.25, 7.7 + 0.25);
        Assert.InRange(beforePeak.PredictedPeakPercent!.Value, 74, 80);
        Assert.False(beforePeak.Peaked);
        Assert.True(beforePeak.PredictedPeakTimeLow <= beforePeak.PredictedPeakTime
            && beforePeak.PredictedPeakTime <= beforePeak.PredictedPeakTimeHigh);
        Assert.True(last!.Peaked);
        Assert.Null(last.PredictedPeakTime);
        Assert.Null(last.PredictedPeakTimeLow);
        Assert.Null(last.PredictedPeakTimeHigh);
    }
}
