using SourdoughMonitor.Analysis;
using Xunit;

namespace SourdoughMonitor.Tests;

public class ReferenceRunTests
{
    [Fact]
    public void FlatDough_PlacesThePeakBySinceFeedingTime()
    {
        var f = ReferenceRun.Forecast(hoursSinceFeeding: 2, risePercent: 2, peakPercent: 75, peakHours: 6.9);
        Assert.Equal(4.9, f.HoursToPeak, 1);
        Assert.True(f.HoursLow < f.HoursToPeak && f.HoursToPeak < f.HoursHigh);
    }

    [Fact]
    public void RisingDough_PlacesThePeakByItsRise_AndLateDoughStaysAhead()
    {
        // +41 % of 75 = 55 % of the reference peak: reached after about 5.4 h on the reference run, even if fed 8 h ago.
        var f = ReferenceRun.Forecast(hoursSinceFeeding: 8, risePercent: 41, peakPercent: 75, peakHours: 6.9);
        Assert.InRange(f.HoursToPeak, 1.2, 1.8);
        var past = ReferenceRun.Forecast(hoursSinceFeeding: 9, risePercent: 80, peakPercent: 75, peakHours: 6.9);
        Assert.Equal(0.25, past.HoursToPeak, 2);
    }
}
