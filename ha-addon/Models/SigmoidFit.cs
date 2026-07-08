using System;
using System.Linq;

namespace SourdoughMonitor.Models
{
    public sealed record SigmoidFit(double L, double K, double T0, double RelativeStdError)
    {
        public double PeakHoursFromStart => T0 + 2.0 / K;
    }
}