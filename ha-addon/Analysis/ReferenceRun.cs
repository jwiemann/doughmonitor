namespace SourdoughMonitor.Analysis;

using SourdoughMonitor.Models;

/// <summary>Peak forecast from a reference run, for the stretch before the rate has turned, where the
/// fitted forecast (<see cref="PeakForecaster"/>) stays silent. The daily starter is fed with the same
/// amount, in the same place and at the same temperature, so it rises along a similar curve; the
/// reference is the run of 2026-11-20 (fed 07:54 UTC, +75 % after 6.9 h, measured from the archive
/// frames against the glass ring). While the rise is still flat the time since feeding places the dough
/// on the curve, afterwards its rise.</summary>
public static class ReferenceRun
{
    /// <summary>Hours since feeding and the share of the peak rise reached by then.</summary>
    private static readonly (double Hours, double Fraction)[] Curve =
    [
        (0, 0), (1, 0.03), (2.5, 0.04), (3, 0.09), (3.5, 0.13), (4, 0.20), (4.5, 0.24),
        (5, 0.41), (5.5, 0.57), (6, 0.72), (6.5, 0.94), (6.9, 1),
    ];

    /// <summary>Below this share of the peak the rise cannot place the dough on the curve: the early
    /// part is flat and noisy, the time since feeding places it instead.</summary>
    private const double PlacementMinFraction = 0.10;

    public static RateForecast Forecast(double hoursSinceFeeding, double risePercent, double peakPercent, double peakHours)
    {
        var scale = peakHours / Curve[^1].Hours;
        var fraction = peakPercent > 0 ? risePercent / peakPercent : 0;
        var byRise = fraction >= PlacementMinFraction;
        var phase = byRise ? HoursAtFraction(fraction) * scale : hoursSinceFeeding;
        var hours = Math.Max(0.25, peakHours - phase);
        var (before, after) = byRise ? (1.0, 1.5) : (1.5, 2.0);
        return new RateForecast(
            hours, Math.Max(0, hours - before), hours + after,
            peakPercent, peakPercent * 0.8, peakPercent * 1.3);
    }

    private static double HoursAtFraction(double fraction)
    {
        for (var i = 1; i < Curve.Length; i++)
        {
            if (Curve[i].Fraction < fraction) continue;
            var (a, b) = (Curve[i - 1], Curve[i]);
            return a.Hours + (b.Hours - a.Hours) * (fraction - a.Fraction) / (b.Fraction - a.Fraction);
        }
        return Curve[^1].Hours;
    }
}
