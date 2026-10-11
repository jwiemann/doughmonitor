using System;
using System.Linq;

namespace SourdoughMonitor.Models
{
    /// <summary>Richards (generalized logistic) rise curve
    /// h(t) = L * (1 + ν * exp(-k(t - t0)))^(-1/ν): ν = 1 is the symmetric logistic,
    /// ν → 0 the Gompertz curve, so the data decides how asymmetric the rise is.</summary>
    public sealed record SigmoidFit(double L, double K, double T0, double Nu, double RelativeStdError)
    {
        /// <summary>Hours from the session start at which the curve reaches the given
        /// fraction of L (solving L*f = L * (1 + ν * exp(-k(t - t0)))^(-1/ν) for t; at
        /// ν = 1 this reduces to the plain logistic solution). Bounded to a sane range:
        /// degenerate fits (k → 0) would otherwise produce unbounded values and overflow
        /// the ETA timestamp.</summary>
        public double HoursAtFraction(double fraction)
        {
            var hours = T0 - Math.Log((Math.Pow(fraction, -Nu) - 1.0) / Nu) / K;
            return Math.Clamp(hours, 0.0, 720.0);
        }

        /// <summary>Hours from the session start of the steepest rise (the curve's inflection
        /// point). For the Richards form above dh/dt is maximal at exp(-k(t - t0)) = 1, i.e.
        /// exactly at t0 for every ν (ν shifts the half-height time away from t0, but never
        /// the steepest point).</summary>
        public double InflectionHours => T0;
    }

    /// <summary>Best fit plus every converged multi-start solution whose error is statistically
    /// indistinguishable from it (approximate 95% confidence region); the spread of their
    /// peak times is the forecast's uncertainty.</summary>
    public sealed record SigmoidFitResult(SigmoidFit Best, IReadOnlyList<SigmoidFit> NearOptimal);
}
