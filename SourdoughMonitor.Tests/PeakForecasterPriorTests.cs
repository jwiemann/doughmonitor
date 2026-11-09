using SourdoughMonitor.Analysis;
using SourdoughMonitor.Models;

namespace SourdoughMonitor.Tests;

public class PeakForecasterPriorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 11, 40, 16, TimeSpan.Zero);

    /// <summary>The starter of 2026-11-09 (minutes from 11:40 UTC, rise in percent): about 4 hours of an
    /// accelerating, still rising curve, rate 5 growing to 22 %/h. Replay output of the archive frames.</summary>
    private static readonly (int Minutes, double Rise)[] Starter2026_11_09 =
    [
        (0, 3.9), (5, 3.9), (10, 3.9), (15, 3.9), (20, 4.3), (25, 5.1), (30, 5.1), (35, 5.8), (40, 6.2), (45, 6.6),
        (50, 7.4), (55, 8.2), (60, 8.9), (65, 10.9), (70, 10.9), (75, 12.8), (80, 13.2), (85, 14.4), (90, 15.6),
        (92, 16.7), (97, 17.9), (102, 18.3), (107, 19.8), (112, 20.6), (117, 22.2), (122, 23.7), (127, 24.5),
        (132, 26.5), (137, 28.0), (138, 29.2), (143, 30.7), (148, 31.1), (153, 32.7), (158, 35.4), (163, 37.0),
        (168, 38.5), (173, 40.1), (178, 40.1), (183, 42.0), (188, 43.2), (193, 45.9), (198, 47.9), (203, 49.4),
        (208, 51.0), (213, 52.9), (218, 54.5), (223, 56.0), (228, 58.0), (233, 59.5), (238, 61.5), (243, 63.0),
        (245, 65.0), (250, 66.9)
    ];

    [Fact]
    public void PeakPrior_PullsAnUndeterminedPeakTowardsTheUsualDoubling()
    {
        // The listed values are smoothed; the analyzer fits the raw readings, so a little scatter is added.
        var samples = Starter2026_11_09
            .Select((p, i) => new Sample(Start.AddMinutes(p.Minutes), p.Rise, p.Rise + 0.9 * Math.Sin(i * 2.3)))
            .ToList();
        var now = samples[^1].Time;

        var withoutPrior = PeakForecaster.TryForecast(samples, now, 0.5, continuing: true);
        var withPrior = PeakForecaster.TryForecast(samples, now, 0.5, continuing: true, peakPriorPercent: 100);

        Assert.NotNull(withoutPrior);
        Assert.NotNull(withPrior);
        // The fit alone puts the peak at about +81 %, a few points above where the dough stands, while
        // the rate is still rising; the doubling a starter usually reaches is the better expectation.
        Assert.True(withPrior!.PeakRisePercent > withoutPrior!.PeakRisePercent + 8,
            $"without prior {withoutPrior.PeakRisePercent:F0} %, with prior {withPrior.PeakRisePercent:F0} %");
        Assert.InRange(withPrior.PeakRisePercent, 88, 110);
        // The prior moves the height, not the timing: both stay within a quarter of an hour.
        Assert.InRange(withPrior.HoursToPeak, withoutPrior.HoursToPeak - 0.3, withoutPrior.HoursToPeak + 0.3);
    }
}
