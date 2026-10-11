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
        var fit = SigmoidFitter.TryFit(RisingSeries())?.Best;
        Assert.NotNull(fit);
        Assert.True(fit!.L > 190, $"plateau {fit.L} must exceed observed max 190");
    }

    [Fact]
    public void TryFit_RisingSeries_PredictsPeakTimeInFuture()
    {
        var samples = RisingSeries();
        var fit = SigmoidFitter.TryFit(samples)?.Best;
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
        var early = SigmoidFitter.TryFit(samples.Take(33).ToList())?.Best;
        var complete = SigmoidFitter.TryFit(samples, early)?.Best;
        Assert.NotNull(early);
        Assert.NotNull(complete);
        var truePeakHours = 6 + Math.Log(0.97 / 0.03) / 0.8;
        Assert.InRange(complete!.L, 98, 102);
        Assert.InRange(complete.HoursAtFraction(0.97), truePeakHours - 0.5, truePeakHours + 0.5);
        Assert.InRange(Math.Abs(complete.HoursAtFraction(0.97) - early!.HoursAtFraction(0.97)), 0, 0.5);
        Assert.True(complete.HoursAtFraction(0.97) < 12);
    }

    [Fact]
    public void TryFit_ResultDoesNotDependOnThePreviousFit()
    {
        // A previous fit from a wrong local solution must not change the answer: the
        // multi-start picks the lowest-error solution regardless of the warm start.
        var samples = Enumerable.Range(0, 49)
            .Select(i => new Sample(Start.AddHours(i / 4.0), 100 / (1 + Math.Exp(-0.8 * (i / 4.0 - 6)))))
            .Take(40)
            .ToList();
        var cold = SigmoidFitter.TryFit(samples);
        var misled = SigmoidFitter.TryFit(samples, new SigmoidFit(400, 0.1, 30, 1.0, 0.5));
        Assert.NotNull(cold);
        Assert.NotNull(misled);
        Assert.InRange(Math.Abs(cold!.Best.HoursAtFraction(0.97) - misled!.Best.HoursAtFraction(0.97)), 0, 0.1);
        Assert.Contains(misled.Best, misled.NearOptimal);
    }

    [Fact]
    public void TryFit_AsymmetricRise_EarlyEtaIsCloserThanLogisticWould()
    {
        // Noise-free Richards rise (L = 100, k = 0.7/h, t0 = 5 h, ν = 0.2) sampled every
        // 15 minutes and truncated at the first sample reaching 80 % of the plateau. The
        // symmetric logistic misreads the slow early phase and calls the 0.97 peak about
        // 1.9 h too early on this truncation (measured with the pre-Richards fitter);
        // the Richards fit must place it within 0.4 h of the closed-form truth.
        const double l = 100, k = 0.7, t0 = 5, nu = 0.2;
        double Height(double t) => l * Math.Pow(1 + nu * Math.Exp(-k * (t - t0)), -1.0 / nu);
        var trueEta = new SigmoidFit(l, k, t0, nu, 0).HoursAtFraction(0.97);
        var samples = new List<Sample>();
        for (var i = 0; i <= 48; i++)
        {
            var t = i * 0.25;
            samples.Add(new Sample(Start.AddHours(t), Height(t)));
            if (Height(t) >= 0.8 * l) break;
        }
        var fit = SigmoidFitter.TryFit(samples)?.Best;
        Assert.NotNull(fit);
        Assert.InRange(fit!.L, 95, 105);
        Assert.True(Math.Abs(fit.HoursAtFraction(0.97) - trueEta) <= 0.4,
            $"ETA {fit.HoursAtFraction(0.97):F2} h vs true {trueEta:F2} h is more than 0.4 h off");
    }
}
