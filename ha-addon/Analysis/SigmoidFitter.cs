using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace SourdoughMonitor.Analysis;

using SourdoughMonitor.Models;

/// <summary>Fits h(t) = L / (1 + exp(-k(t - t0))) to rise samples via Nelder-Mead on SSE.</summary>
public static class SigmoidFitter
{
    public static SigmoidFit? TryFit(IReadOnlyList<Sample> samples)
    {
        if (samples.Count < 5) return null;
        var start = samples[0].Time;
        var t = samples.Select(s => (s.Time - start).TotalHours)
            .ToArray();
        var h = samples.Select(s => s.RisePercent)
            .ToArray();
        var maxH = h.Max();
        if (maxH < 5) return null;
        var initial = Vector<double>.Build.DenseOfArray([Math.Max(maxH * 1.5, 30), 1.0, Math.Max(t[^1], 1.0)]);
        try
        {
            var objective = ObjectiveFunction.Value(p => SumSquaredError(p, t, h));
            var result = NelderMeadSimplex.Minimum(objective, initial, 1e-8, 5000);
            var p = result.MinimizingPoint;
            var (l, k, t0) = (p[0], p[1], p[2]);
            if (l <= 0 || k <= 0 || l > 500 || double.IsNaN(l) || double.IsNaN(k)) return null;
            var rmse = Math.Sqrt(SumSquaredError(p, t, h) / t.Length);
            var relError = rmse / l;
            return new SigmoidFit(l, k, t0, relError);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double SumSquaredError(Vector<double> p, double[] t, double[] h)
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
        return sse;
    }
}
