using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Tests;

/// <summary>The surface-loss flag fires only for an unbroken no_surface streak after a
/// confirmed peak; anything else — dark night, missing jar, a fresh measurement — resets
/// the streak instead of being counted as a loss.</summary>
public class SurfaceLossMonitorTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SurfaceLossMonitor NewMonitor() =>
        new(new AnalysisOptions { StateFilePath = null });

    [Fact]
    public void PeakedSurfaceLoss_IsReportedOnceTheStreakReachesTheThreshold()
    {
        var monitor = NewMonitor();
        for (var minute = 0; minute < 120; minute++)
            Assert.Null(monitor.Observe("no_surface", true, Start.AddMinutes(minute)));
        var reported = monitor.Observe("no_surface", true, Start.AddMinutes(120));
        Assert.NotNull(reported);
        Assert.Equal(120, reported!.Value, precision: 0);
    }

    [Fact]
    public void DarkFrame_ResetsTheStreak()
    {
        var monitor = NewMonitor();
        for (var minute = 0; minute <= 100; minute++)
            Assert.Null(monitor.Observe("no_surface", true, Start.AddMinutes(minute)));
        Assert.Null(monitor.Observe("dark_frame", true, Start.AddMinutes(101)));
        // The second 100 minute stretch starts from the reset, not at 200+ minutes.
        for (var minute = 102; minute <= 202; minute++)
            Assert.Null(monitor.Observe("no_surface", true, Start.AddMinutes(minute)));
    }

    [Fact]
    public void WithoutAPeak_TheLossIsNeverReported()
    {
        var monitor = NewMonitor();
        for (var minute = 0; minute <= 300; minute++)
            Assert.Null(monitor.Observe("no_surface", false, Start.AddMinutes(minute)));
    }

    [Fact]
    public void MeasurementAfterAFlaggedStreak_ClearsTheFlag()
    {
        var monitor = NewMonitor();
        double? reported = null;
        for (var minute = 0; minute <= 120; minute++)
            reported = monitor.Observe("no_surface", true, Start.AddMinutes(minute));
        Assert.NotNull(reported);
        Assert.Null(monitor.Observe("detected", true, Start.AddMinutes(121)));
    }
}
