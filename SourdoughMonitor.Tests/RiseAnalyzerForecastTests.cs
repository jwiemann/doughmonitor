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
        RiseReading? atEightHours = null;
        RiseReading? atNineHours = null;
        RiseReading? last = null;
        for (var step = 0; step <= 72; step++)
        {
            last = analyzer.Analyze(KnownGrowth(step));
            if (step == 48) atEightHours = last;
            if (step == 54) atNineHours = last;
        }
        Assert.NotNull(atEightHours?.PredictedPeakTime);
        Assert.NotNull(atNineHours?.PredictedPeakTime);
        var truePeakHours = 6 + Math.Log(0.97 / 0.03) / 0.8;
        var predictedHours = (atEightHours!.PredictedPeakTime!.Value - Start).TotalHours;
        Assert.InRange(predictedHours, truePeakHours - 0.5, truePeakHours + 1);
        Assert.InRange(atEightHours.PredictedPeakPercent!.Value, 92, 101);
        Assert.False(atEightHours.Peaked);
        Assert.InRange(Math.Abs((atNineHours!.PredictedPeakTime!.Value - atEightHours.PredictedPeakTime.Value).TotalHours), 0, 0.75);
        Assert.True(last!.Peaked);
        Assert.Null(last.PredictedPeakTime);
    }
}
