namespace SourdoughMonitor.Models;

/// <summary>Peak forecast from the rate model (see <see cref="Analysis.PeakForecaster"/>): hours
/// from now until the rise stops (best fit, plus the earliest/latest the data still allow) and the
/// rise (%) the dough will have reached by then.</summary>
public sealed record RateForecast(
    double HoursToPeak,
    double HoursLow,
    double HoursHigh,
    double PeakRisePercent,
    double PeakRiseLowPercent,
    double PeakRiseHighPercent);
