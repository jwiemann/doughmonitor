using SourdoughMonitor.Analysis;
using SourdoughMonitor.Models;

namespace SourdoughMonitor.Tests;

public class SigmoidFitterTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<Sample> RisingSeries()
    {
        // Still clearly rising at the end — the fitter must anticipate remaining rise.
        double[] rise = [0, 10, 25, 45, 70, 95, 120, 145, 165, 180, 190];
        return rise.Select((value, i) => new Sample(Start.AddMinutes(30 * i), value))
            .ToList();
    }

    [Fact]
    public void TryFit_RisingSeries_PredictsPeakAboveCurrentRise()
    {
        var fit = SigmoidFitter.TryFit(RisingSeries());
        Assert.NotNull(fit);
        Assert.True(fit!.L > 190, $"plateau {fit.L} must exceed observed max 190");
    }

    [Fact]
    public void TryFit_RisingSeries_PredictsPeakTimeInFuture()
    {
        var samples = RisingSeries();
        var fit = SigmoidFitter.TryFit(samples);
        Assert.NotNull(fit);
        var lastHours = (samples[^1].Time - samples[0].Time).TotalHours;
        // This series is still rising well short of its fitted practical peak.
        var peakHours = fit!.HoursAtFraction(0.97);
        Assert.True(peakHours >= lastHours - 0.1,
            $"peak at {peakHours}h must not precede last sample {lastHours}h");
    }


    [Fact]
    public void TryFit_TooFewSamples_ReturnsNull()
    {
        var samples = RisingSeries().Take(4).ToList();
        Assert.Null(SigmoidFitter.TryFit(samples));
    }

    [Fact]
    public void TryFit_FlatSeries_ReturnsNull()
    {
        var samples = Enumerable.Range(0, 10)
            .Select(i => new Sample(Start.AddMinutes(30 * i), 3.0))
            .ToList();
        Assert.Null(SigmoidFitter.TryFit(samples));
    }

    [Fact]
    public void TryFit_ObservedPlateauDoesNotPushMaximumOrPeakTimeForward()
    {
        var samples = Enumerable.Range(0, 49)
            .Select(i => new Sample(Start.AddHours(i / 4.0), 100 / (1 + Math.Exp(-0.8 * (i / 4.0 - 6)))))
            .ToList();
        var early = SigmoidFitter.TryFit(samples.Take(33).ToList());
        var complete = SigmoidFitter.TryFit(samples, early);
        Assert.NotNull(early);
        Assert.NotNull(complete);
        var truePeakHours = 6 + Math.Log(0.97 / 0.03) / 0.8;
        Assert.InRange(complete!.L, 98, 102);
        Assert.InRange(complete.HoursAtFraction(0.97), truePeakHours - 0.5, truePeakHours + 0.5);
        Assert.InRange(Math.Abs(complete.HoursAtFraction(0.97) - early!.HoursAtFraction(0.97)), 0, 0.5);
        Assert.True(complete.HoursAtFraction(0.97) < 12);
    }
}
