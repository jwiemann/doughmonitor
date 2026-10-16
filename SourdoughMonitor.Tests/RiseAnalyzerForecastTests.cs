using System.Globalization;
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
    public void RateFallingToZero_ForecastsPeakInsideItsRangeBeforeItArrivesThenConfirmsIt()
    {
        var analyzer = new RiseAnalyzer(new AnalysisOptions { StateFilePath = null });
        RiseReading? beforePeak = null;
        RiseReading? last = null;
        for (var step = 0; step <= 12 * 10; step++)
        {
            last = analyzer.Analyze(RampThenFall(step));
            if (step == 12 * 7 + 3) beforePeak = last; // 7.25 h: the rate reaches zero at 7.7 h
        }
        Assert.NotNull(beforePeak?.PredictedPeakTime);
        var low = (beforePeak!.PredictedPeakTimeLow!.Value - Start).TotalHours;
        var high = (beforePeak.PredictedPeakTimeHigh!.Value - Start).TotalHours;
        Assert.InRange(7.7, low, high);
        Assert.InRange(beforePeak.PredictedPeakPercent!.Value, 74, 82);
        Assert.False(beforePeak.Peaked);
        Assert.True(beforePeak.PredictedPeakTimeLow <= beforePeak.PredictedPeakTime
            && beforePeak.PredictedPeakTime <= beforePeak.PredictedPeakTimeHigh);
        Assert.True(last!.Peaked);
        Assert.Null(last.PredictedPeakTime);
        Assert.Null(last.PredictedPeakTimeLow);
        Assert.Null(last.PredictedPeakTimeHigh);
    }

    /// <summary>2026-10-14: the rise slowed to a stall at 19 % for about an hour (15:15-16:00 UTC),
    /// then resumed at 20 %/h. A stall that early is not the peak of a daily-fed jar (100-130 %).</summary>
    private static IEnumerable<LevelMeasurement> StallAt19Percent()
    {
        for (var step = 0; step <= 12 * 6; step++)
        {
            var hours = step / 12.0;
            var rise = hours <= 3 ? 19 * (1 - Math.Pow(1 - hours / 3, 2))
                : hours <= 4 ? 19
                : 19 + 20 * (hours - 4);
            var noise = (step * 7 % 5 - 2) * 0.5;
            yield return new LevelMeasurement(Start.AddHours(hours), 300 - 100 * (1 + rise / 100.0) + 100 + noise, 0, 400);
        }
    }

    [Fact]
    public void MidRiseStallAtLowRise_PublishesNoForecast()
    {
        static int StallForecasts(AnalysisOptions options)
        {
            var analyzer = new RiseAnalyzer(options);
            return StallAt19Percent()
                .Select(analyzer.Analyze)
                .Count(r => r is { PredictedPeakTime: not null } && r.Time < Start.AddHours(4.2));
        }

        Assert.True(StallForecasts(new AnalysisOptions { StateFilePath = null, MinRisePercentForForecast = 0 }) > 0,
            "the stall must fool the forecaster without the prior, else this test proves nothing");
        Assert.Equal(0, StallForecasts(new AnalysisOptions { StateFilePath = null }));
    }

    /// <summary>The measured 2026-10-14 session: fed 14:00 local, five-minute vision-LLM readings
    /// (3 px of scatter each). The rate holds at 20-24 %/h for two hours (19:00-21:20 UTC) and
    /// then falls to zero; the rise stops at about 22:30 UTC at 120-125 %.</summary>
    [Fact]
    public void MeasuredSession_ForecastsOnlyOnceTheRateFell_AndTheRangeHoldsThePeak()
    {
        var analyzer = new RiseAnalyzer(new AnalysisOptions { StateFilePath = null });
        var readings = new List<RiseReading>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rise_2026-10-14_fed-12h.csv")))
        {
            var fields = line.Split(',');
            var measurement = new LevelMeasurement(
                DateTimeOffset.Parse(fields[0], CultureInfo.InvariantCulture),
                double.Parse(fields[1], CultureInfo.InvariantCulture), 0,
                double.Parse(fields[2], CultureInfo.InvariantCulture));
            if (analyzer.Analyze(measurement) is { } reading) readings.Add(reading);
        }

        static DateTimeOffset Utc(int day, int hour, int minute) =>
            new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

        // A steady 20 %/h rise is not a peak, however many hours it lasts.
        Assert.All(readings.Where(r => r.Time < Utc(14, 21, 20)), r => Assert.Null(r.PredictedPeakTime));

        // Within the hour before the rise stops the forecast exists and brackets the stop.
        var duringFall = readings.Where(r => r.Time >= Utc(14, 21, 30) && r.Time <= Utc(14, 22, 20)).ToList();
        Assert.True(duringFall.Count(r => r.PredictedPeakTime is not null) >= duringFall.Count * 0.8);
        foreach (var reading in duringFall.Where(r => r.PredictedPeakTime is not null))
        {
            Assert.InRange(Utc(14, 22, 30), reading.PredictedPeakTimeLow!.Value, reading.PredictedPeakTimeHigh!.Value);
            Assert.InRange(reading.PredictedPeakPercent!.Value, 115, 130);
        }

        // The two misdetections of that session (433 and 439 px, 20 px above the plateau) never
        // entered the curve.
        Assert.DoesNotContain(readings, r => r.Time.Hour == 23 && r.Time.Minute == 57);
        Assert.DoesNotContain(readings, r => r.Time.Day == 15 && r.Time.Hour == 0 && r.Time.Minute == 22);

        Assert.Contains(readings, r => r.Peaked);
    }

    /// <summary>The evening re-feed of 2026-10-15 peaked at only 60 % (the earlier prior of a 70 %
    /// minimum rise left it without any forecast) and the rise stopped abruptly at about 23:30 UTC.
    /// The ETA must exist from the turn on and must not vanish once the stop is seen, up to the
    /// "peaked" flag 1.5 h later.</summary>
    [Fact]
    public void EveningRefeedPeakingAt60Percent_KeepsAnEtaUntilPeaked()
    {
        var analyzer = new RiseAnalyzer(new AnalysisOptions { StateFilePath = null });
        var readings = new List<RiseReading>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rise_2026-10-15_evening-refeed.csv")))
        {
            var fields = line.Split(',');
            var measurement = new LevelMeasurement(
                DateTimeOffset.Parse(fields[0], CultureInfo.InvariantCulture),
                double.Parse(fields[1], CultureInfo.InvariantCulture), 0,
                double.Parse(fields[2], CultureInfo.InvariantCulture));
            if (analyzer.Analyze(measurement) is { } reading && !reading.NewSession) readings.Add(reading);
        }

        static DateTimeOffset Utc(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

        // Nothing while the rise is steady.
        Assert.All(readings.Where(r => r.Time < Utc(15, 23, 30)), r => Assert.Null(r.PredictedPeakTime));

        // From the turn until the "peaked" flag every cycle shows an ETA near the real stop.
        var firstEta = readings.First(r => r.PredictedPeakTime is not null);
        Assert.True(firstEta.Time <= Utc(15, 23, 55), $"first ETA at {firstEta.Time:t}");
        var peaked = readings.First(r => r.Peaked);
        var between = readings.Where(r => r.Time >= Utc(15, 23, 55) && r.Time < peaked.Time && !r.Peaked).ToList();
        Assert.NotEmpty(between);
        Assert.All(between, r => Assert.NotNull(r.PredictedPeakTime));
        Assert.All(readings.Where(r => r.PredictedPeakTime is not null),
            r => Assert.InRange(r.PredictedPeakTime!.Value, Utc(15, 23, 30), Utc(16, 0, 45)));
        Assert.InRange(firstEta.PredictedPeakPercent!.Value, 50, 70);
        Assert.True(peaked.Time <= Utc(16, 1, 30), $"peaked at {peaked.Time:t}");
    }
}
