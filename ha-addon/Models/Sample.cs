using System;
using System.Linq;

namespace SourdoughMonitor.Models;

/// <summary>One accepted reading. <paramref name="RisePercent"/> is the median-smoothed rise
/// (lags a trend by about half the smoothing window), <paramref name="RawRisePercent"/> the
/// unsmoothed one (null in state files written before it existed).</summary>
public sealed record Sample(DateTimeOffset Time, double RisePercent, double? RawRisePercent = null);