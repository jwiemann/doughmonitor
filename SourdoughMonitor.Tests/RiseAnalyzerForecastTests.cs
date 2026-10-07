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

    [Fact]
    public void ObservedSlowdown_PredictsPracticalPeakThenConfirmsIt()
    {
        var analyzer = new RiseAnalyzer(new AnalysisOptions { StateFilePath = null });
        RiseReading? atNineHours = null;
        RiseReading? atTenHours = null;
        RiseReading? last = null;
        for (var step = 0; step <= 72; step++)
        {
            last = analyzer.Analyze(KnownGrowth(step));
            if (step == 54) atNineHours = last;
            if (step == 60) atTenHours = last;
        }
        // The rate has to fall measurably below its maximum before any ETA is shown.
        Assert.NotNull(atNineHours?.PredictedPeakTime);
        Assert.NotNull(atTenHours?.PredictedPeakTime);
        var truePeak = Start.AddHours(6 + Math.Log(0.97 / 0.03) / 0.8);
        var predictedHours = (atNineHours!.PredictedPeakTime!.Value - Start).TotalHours;
        Assert.InRange(predictedHours, (truePeak - Start).TotalHours - 0.5, (truePeak - Start).TotalHours + 1);
        Assert.InRange(atNineHours.PredictedPeakPercent!.Value, 92, 101);
        Assert.False(atNineHours.Peaked);
        Assert.InRange(Math.Abs((atTenHours!.PredictedPeakTime!.Value - atNineHours.PredictedPeakTime.Value).TotalHours), 0, 0.75);
        Assert.True(atNineHours.PredictedPeakTimeLow <= atNineHours.PredictedPeakTime
            && atNineHours.PredictedPeakTime <= atNineHours.PredictedPeakTimeHigh);
        Assert.True(last!.Peaked);
        Assert.Null(last.PredictedPeakTime);
        Assert.Null(last.PredictedPeakTimeLow);
        Assert.Null(last.PredictedPeakTimeHigh);
    }
}
