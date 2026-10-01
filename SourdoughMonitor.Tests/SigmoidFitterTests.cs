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
        // The constraint is a penalty, not a hard bound; small (<6 min) overshoots
        // are acceptable — the sensor granularity is minutes. 0.97 is the production
        // PeakFraction: the "practical peak" the analyzer reports.
        var peakHours = fit!.HoursAtFraction(0.97);
        Assert.True(peakHours >= lastHours - 0.1,
            $"peak at {peakHours}h must not precede last sample {lastHours}h");
    }

    [Fact]
    public void TryFit_ConvergedSeries_StillHonorsPlateauFloor()
    {
        // Production stops fitting once slopes flatten; the fit is still required to
        // place the plateau above everything observed whenever it runs.
        double[] rise = [0, 30, 90, 160, 215, 250, 265, 270, 271, 272];
        var samples = rise.Select((value, i) => new Sample(Start.AddMinutes(30 * i), value))
            .ToList();
        var fit = SigmoidFitter.TryFit(samples);
        Assert.NotNull(fit);
        Assert.True(fit!.L >= 272 * 1.02, $"plateau {fit.L} below observed max");
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
}
