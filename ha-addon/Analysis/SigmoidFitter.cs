using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace SourdoughMonitor.Analysis;

using SourdoughMonitor.Models;

/// <summary>Fits h(t) = L / (1 + exp(-k(t - t0))) to rise samples via Nelder-Mead on SSE.</summary>
public static class SigmoidFitter
{
    /// <summary>Fits the sigmoid to the given samples. When <paramref name="previousFit"/> is
    /// supplied, the optimizer is also seeded from it (in addition to the generic heuristic
    /// seed) and the lower-error result is kept. Consecutive calls only ever add one sample,
    /// so the previous solution is normally very close to the new optimum; warm-starting from
    /// it keeps the fit from jumping to an unrelated local minimum between cycles.</summary>
    public static SigmoidFit? TryFit(IReadOnlyList<Sample> samples, SigmoidFit? previousFit = null)
    {
        if (samples.Count < 5) return null;
        var start = samples[0].Time;
        var t = samples.Select(s => (s.Time - start).TotalHours)
            .ToArray();
        var h = samples.Select(s => s.RisePercent)
            .ToArray();
        var maxH = h.Max();
        if (maxH < 5) return null;
        var seeds = new List<Vector<double>>
        {
            Vector<double>.Build.DenseOfArray([Math.Max(maxH * 1.5, 30), 1.0, Math.Max(t[^1], 1.0)])
        };
        if (previousFit is not null)
            seeds.Add(Vector<double>.Build.DenseOfArray([previousFit.L, previousFit.K, previousFit.T0]));
        SigmoidFit? best = null;
        var bestSse = double.PositiveInfinity;
        foreach (var seed in seeds)
        {
            try
            {
                var objective = ObjectiveFunction.Value(p => SumSquaredError(p, t, h, maxH));
                var result = NelderMeadSimplex.Minimum(objective, seed, 1e-8, 5000);
                var p = result.MinimizingPoint;
                var (l, k, t0) = (p[0], p[1], p[2]);
                // k below 0.05/h means a >60h time constant: the "fit" is a flat line and
                // its peak ETA is unbounded (HoursAtFraction explodes) — no information.
                if (l <= 0 || k < 0.05 || l > 500 || double.IsNaN(l) || double.IsNaN(k)) continue;
                var sse = SumSquaredError(p, t, h, maxH);
                if (sse >= bestSse) continue;
                bestSse = sse;
                var rmse = Math.Sqrt(sse / t.Length);
                best = new SigmoidFit(l, k, t0, rmse / l);
            }
            catch (Exception)
            {
            }
        }
        return best;
    }

    private static double SumSquaredError(Vector<double> p, double[] t, double[] h, double maxH)
    {
        var (l, k, t0) = (p[0], p[1], p[2]);
        if (l <= 0 || k <= 0) return double.MaxValue;
        double sse = 0;
        for (var i = 0; i < t.Length; i++)
        {
            var predicted = l / (1 + Math.Exp(-k * (t[i] - t0)));
            var diff = predicted - h[i];
            sse += diff * diff;
        }
        // Physics constraints. The fitter only runs while the dough is still rising, so
        // the plateau must exceed everything observed and the ~88%-of-plateau peak must
        // lie in the future. Without these, Nelder-Mead settles on "plateau = current
        // value, inflection in the past" — a perfect fit to the partial segment that
        // predicts a peak that has already happened.
        if (l < maxH * 1.02)
            sse += Math.Pow(maxH * 1.02 - l, 2) * t.Length;
        var peakTime = t0 + 2.0 / k;
        if (peakTime < t[^1])
            sse += Math.Pow(t[^1] - peakTime, 2) * maxH * maxH;
        return sse;
    }
}
