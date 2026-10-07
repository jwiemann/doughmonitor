using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Tests;

/// <summary>Peak-forecast reliability on realistic, noisy rise shapes (small kept-starter rise,
/// long acceleration) and the end-of-session prediction summary.</summary>
public class RiseAnalyzerPredictionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private const double BaselinePx = 104;

    private static AnalysisOptions Options() => new() { StateFilePath = null };

    private static LevelMeasurement AtRisePercent(DateTimeOffset time, double risePercent, double noisePx) =>
        new(time, 1000 - BaselinePx * (1 + risePercent / 100.0) - noisePx, 0, 1000);

    /// <summary>A kept-starter bake: +6.2% logistic over about 2h, true 97% point at 1.5h + ln(32.3)/3.5h.</summary>
    private static IEnumerable<(DateTimeOffset Time, double Rise, double Noise)> LittleRise(int minutes)
    {
        var random = new Random(7);
        for (var minute = 0; minute <= minutes; minute++)
        {
            var hours = minute / 60.0;
            yield return (Start.AddMinutes(minute), 6.2 / (1 + Math.Exp(-3.5 * (hours - 1.5))),
                random.NextDouble() * 2 - 1);
        }
    }

    private static readonly double LittleRiseTruePeakHours = 1.5 + Math.Log(0.97 / 0.03) / 3.5;

    private static List<RiseReading> Run(RiseAnalyzer analyzer, IEnumerable<(DateTimeOffset, double, double)> series)
    {
        var readings = new List<RiseReading>();
        foreach (var (time, rise, noise) in series)
        {
            var reading = analyzer.Analyze(AtRisePercent(time, rise, noise));
            if (reading is not null) readings.Add(reading);
        }
        return readings;
    }

    [Fact]
    public void SmallRise_EventuallyPublishesPointEtaNearTrueMaximumWithContainingRange()
    {
        var analyzer = new RiseAnalyzer(Options());
        var readings = Run(analyzer, LittleRise(240));
        var published = readings.Where(r => r.PredictedPeakTime is not null).ToList();
        Assert.NotEmpty(published);
        var truePeak = Start.AddHours(LittleRiseTruePeakHours);
        var last = published[^1];
        Assert.InRange(Math.Abs((last.PredictedPeakTime!.Value - truePeak).TotalHours), 0, 1.0);
        Assert.NotNull(last.PredictedPeakTimeLow);
        Assert.NotNull(last.PredictedPeakTimeHigh);
        // Noise biases the fitted 97% point late by ~0.2h; the range must contain the true
        // maximum to within that bias.
        Assert.True(last.PredictedPeakTimeLow - TimeSpan.FromMinutes(15) <= truePeak
            && truePeak <= last.PredictedPeakTimeHigh + TimeSpan.FromMinutes(15),
            $"range {last.PredictedPeakTimeLow:t}-{last.PredictedPeakTimeHigh:t} must contain {truePeak:t}");
        Assert.True(last.PredictedPeakTimeLow <= last.PredictedPeakTime && last.PredictedPeakTime <= last.PredictedPeakTimeHigh);
    }

    [Fact]
    public void AcceleratingRise_NeverPeaksAndHidesPointEtaWhileRateStillClimbs()
    {
        // Rate climbs linearly from 6 to 24 %/h over 3h (rise 45%): the afternoon shape
        // that used to trip "practically peaked" off a degenerate fit.
        var analyzer = new RiseAnalyzer(Options());
        var random = new Random(3);
        var series = Enumerable.Range(0, 181).Select(minute =>
        {
            var hours = minute / 60.0;
            return (Start.AddMinutes(minute), 6 * hours + 3 * hours * hours, (random.NextDouble() * 2 - 1) * 0.5);
        });
        var readings = Run(analyzer, series);
        Assert.NotEmpty(readings);
        Assert.All(readings, r =>
        {
            Assert.False(r.Peaked);
            Assert.Null(r.PredictedPeakTime);
        });
    }

    [Fact]
    public void RateSpikeFollowedByWobble_NeverFlagsPeakedMidRise()
    {
        // The exact shape that flickered "Peaked" on 2026-10-06 (replay-measured rise series
        // 14:50-16:15 UTC): a first growth spurt with a detection gap inflates one slope
        // sample, the rate then wobbles just below it while the dough keeps accelerating,
        // and a degenerate fit reaches the current rise. Neither a spurt-then-wobble turn
        // nor a fit that merely catches up with the data may declare a peak mid-rise.
        (int Minute, double Rise)[] measured =
        [
            (0, 13.3), (3, 19.4), (4, 20.5), (5, 20.9), (8, 20.9), (9, 21.7), (10, 21.7), (11, 22.1),
            (12, 22.4), (13, 22.4), (14, 22.4), (15, 22.8), (24, 22.8), (25, 22.4), (26, 22.8), (27, 22.8),
            (28, 22.8), (29, 23.2), (30, 23.2), (31, 23.6), (32, 23.6), (33, 24.0), (34, 24.0), (35, 24.3),
            (36, 24.3), (37, 24.7), (38, 25.1), (39, 25.1), (40, 25.5), (41, 25.5), (42, 25.9), (43, 26.2),
            (44, 27.0), (45, 27.0), (46, 27.4), (47, 27.4), (48, 27.8), (49, 28.1), (50, 28.5), (51, 28.9),
            (52, 28.9), (53, 28.9), (54, 29.3), (55, 30.0), (56, 30.0), (57, 30.4), (58, 30.8), (59, 30.8),
            (60, 31.2), (61, 31.6), (62, 31.9), (63, 32.3), (64, 32.3), (65, 32.7), (66, 32.7), (67, 33.1),
            (68, 33.5), (69, 33.8), (70, 33.8), (71, 34.2), (72, 34.6), (73, 35.0), (74, 35.4), (75, 35.4),
            (76, 35.7), (77, 36.1), (78, 36.5), (79, 36.9), (80, 36.9), (81, 37.3), (82, 38.0)
        ];
        const double sessionBaselinePx = 263;
        var analyzer = new RiseAnalyzer(Options());
        foreach (var (minute, rise) in measured)
        {
            var height = 1000 - sessionBaselinePx * (1 + rise / 100.0);
            var reading = analyzer.Analyze(new LevelMeasurement(Start.AddMinutes(minute), height, 0, 1000));
            // The post-gap lull (minute ~25) may declare a provisional peak that the
            // renewed growth revokes — that peak/unpeak pairing is the designed behaviour.
            // The regression is a peak during the accelerating second spurt.
            if (minute < 55) continue;
            Assert.True(reading is null || !reading.Peaked,
                $"peaked flagged at +{minute} min (rise {rise}%) while the dough was still accelerating");
        }
    }

    [Fact]
    public void SessionSummary_ReportsMaximumAndPredictionErrorWhenTheSessionEnds()
    {
        var analyzer = new RiseAnalyzer(Options());
        var readings = Run(analyzer, LittleRise(240));
        var lastEta = readings.Last(r => r.PredictedPeakTime is not null).PredictedPeakTime!.Value;
        Assert.Null(analyzer.DequeueSessionSummary());

        // Feeding: the jar empties below the baseline and stays there until confirmed.
        var feedTime = Start.AddMinutes(241);
        for (var i = 0; i < 3; i++)
            analyzer.Analyze(AtRisePercent(feedTime.AddMinutes(i), -50, 0));

        var summary = analyzer.DequeueSessionSummary();
        Assert.NotNull(summary);
        Assert.Equal(Start, summary!.SessionStart);
        Assert.Equal(feedTime.AddMinutes(2), summary.SessionEnd);
        Assert.InRange(summary.MaxRisePercent, 5.5, 8);
        Assert.InRange(Math.Abs(readings.Max(r => r.RisePercent) - summary.MaxRisePercent), 0, 0.06);
        Assert.InRange((summary.MaxRiseTime - Start).TotalHours, 1.5, 4.0);
        Assert.Equal(lastEta, summary.PredictedPeakTime);
        Assert.Equal((lastEta - summary.MaxRiseTime).TotalMinutes, summary.PredictionErrorMinutes!.Value, 6);
        Assert.True(summary.MaxRatePercentPerHour > 0);
        Assert.Null(analyzer.DequeueSessionSummary());
    }

    [Fact]
    public void SessionSummary_IsNotKeptForATrivialSession()
    {
        var analyzer = new RiseAnalyzer(Options());
        Run(analyzer, LittleRise(2));
        analyzer.Reset();
        Assert.Null(analyzer.DequeueSessionSummary());
    }
}
