using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace SourdoughMonitor.Analysis;

using SourdoughMonitor.Models;

/// <summary>Fits h(t) = L / (1 + exp(-k(t - t0))) to rise samples via multi-start Nelder-Mead on SSE.</summary>
public static class SigmoidFitter
{
    /// <summary>SSE inflation (relative to the best fit) of the approximate 95% least-squares
    /// confidence region for 3 parameters: SSE &lt;= SSEmin * (1 + F/(n-3)) with F ~ 3 * F(3, n-3)
    /// ~ 3 * 3.0 (F(0.95; 3, n-3) is about 2.6-3.1 for n between 10 and 100).</summary>
    private const double ConfidenceFactor = 3.0 * 3.0;

    /// <summary>Fits the sigmoid from several independent seeds (heuristic, wide plateau, slow
    /// growth and, when supplied, <paramref name="previousFit"/>). Taking the lowest-error
    /// solution removes the path dependence a single warm start would have; the other
    /// solutions statistically consistent with the data are returned as
    /// <see cref="SigmoidFitResult.NearOptimal"/> so callers can express forecast uncertainty.</summary>
    public static SigmoidFitResult? TryFit(IReadOnlyList<Sample> samples, SigmoidFit? previousFit = null)
    {
        if (samples.Count < 5) return null;
        var start = samples[0].Time;
        var t = samples.Select(s => (s.Time - start).TotalHours)
            .ToArray();
        var h = samples.Select(s => s.RisePercent)
            .ToArray();
        var maxH = h.Max();
        if (maxH < 5) return null;
        var tLast = t[^1];
        var seeds = new List<double[]>
        {
            new[] { Math.Max(maxH * 1.5, 30), 1.0, Math.Max(tLast, 1.0) },
            new[] { Math.Max(maxH * 3, 50), 1.0, Math.Max(tLast, 1.0) },
            new[] { Math.Max(maxH * 1.5, 30), 0.3, Math.Max(tLast * 1.2, 2.0) }
        };
        if (previousFit is not null)
            seeds.Add([previousFit.L, previousFit.K, previousFit.T0]);
        var solutions = new List<(SigmoidFit Fit, double Sse)>();
        foreach (var seed in seeds)
        {
            try
            {
                var objective = ObjectiveFunction.Value(p => SumSquaredError(p, t, h, maxH));
                var result = NelderMeadSimplex.Minimum(objective, Vector<double>.Build.DenseOfArray(seed), 1e-8, 5000);
                var p = result.MinimizingPoint;
                var (l, k, t0) = (p[0], p[1], p[2]);
                // k below 0.05/h means a >60h time constant: the "fit" is a flat line and
                // its peak ETA is unbounded (HoursAtFraction explodes) — no information.
                if (l <= 0 || k < 0.05 || l > 500 || double.IsNaN(l) || double.IsNaN(k)) continue;
                var sse = SumSquaredError(p, t, h, maxH);
                var rmse = Math.Sqrt(sse / t.Length);
                solutions.Add((new SigmoidFit(l, k, t0, rmse / l), sse));
            }
            catch (Exception)
            {
            }
        }
        if (solutions.Count == 0) return null;
        var best = solutions.MinBy(s => s.Sse);
        var limit = best.Sse * (1 + ConfidenceFactor / Math.Max(1, samples.Count - 3));
        var near = solutions.Where(s => s.Sse <= limit && !ReferenceEquals(s.Fit, best.Fit))
            .Select(s => s.Fit)
            .Prepend(best.Fit)
            .ToList();
        return new SigmoidFitResult(best.Fit, near);
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
        // A fitted plateau cannot lie below observed rise. Its practical peak can
        // legitimately be in the past: forcing a future ETA invents further growth
        // once the actual plateau is reached. Publication confidence is gated by the
        // analyzer's observed slowdown, not by distorting the fitted curve.
        if (l < maxH)
            sse += Math.Pow(maxH - l, 2) * t.Length;
        return sse;
    }
}
