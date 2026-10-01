using System;
using System.Linq;

namespace SourdoughMonitor.Models
{
    public sealed record SigmoidFit(double L, double K, double T0, double RelativeStdError)
    {
        /// <summary>Hours from the session start at which the curve reaches the given
        /// fraction of L (solving L*f = L / (1 + exp(-k(t - t0))) for t). Bounded to a
        /// sane range: degenerate fits (k → 0) would otherwise produce unbounded values
        /// and overflow the ETA timestamp.</summary>
        public double HoursAtFraction(double fraction)
        {
            var hours = T0 - Math.Log(1.0 / fraction - 1.0) / K;
            return Math.Clamp(hours, 0.0, 720.0);
        }
    }
}