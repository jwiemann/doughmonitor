namespace SourdoughMonitor.Models;

/// <summary>Peak forecast from the rise-rate trend: hours from now until the rate reaches zero
/// (median over the fit windows, plus the lowest/highest window) and the rise (%) the dough will
/// have reached by then.</summary>
public sealed record RateForecast(
    double HoursToPeak,
    double HoursLow,
    double HoursHigh,
    double PeakRisePercent,
    double PeakRiseLowPercent,
    double PeakRiseHighPercent);
