using SourdoughMonitor.Services;

namespace SourdoughMonitor.Tests;

public class LlmUsageTrackerTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 14);

    [Fact]
    public void NewDay_ResetsTodayButKeepsTotals()
    {
        var tracker = new LlmUsageTracker(null);
        tracker.Record(3, 0.0072, Day1);
        tracker.Record(3, 0.0072, Day1);

        var next = tracker.Snapshot(Day1.AddDays(1));

        Assert.Equal(0, next.CallsToday);
        Assert.Equal(0, next.CostTodayUsd);
        Assert.Equal(6, next.CallsTotal);
        Assert.Equal(0.0144, next.CostTotalUsd, 6);
    }

    [Fact]
    public void Totals_SurviveARestartThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"llm-usage-{Guid.NewGuid():N}.json");
        try
        {
            new LlmUsageTracker(path).Record(3, 0.0072, Day1);

            var restarted = new LlmUsageTracker(path).Snapshot(Day1);

            Assert.Equal(3, restarted.CallsToday);
            Assert.Equal(0.0072, restarted.CostTotalUsd, 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
