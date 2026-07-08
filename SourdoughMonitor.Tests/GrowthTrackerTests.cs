using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Tests;

public class GrowthTrackerTests
{
    [Fact]
    public void Analyze_ReturnsCollectingAnalysisBeforeEnoughSamples()
    {
        var tracker = new GrowthTracker(
            new GrowthOptions
            {
                MinSamplesForFit = 10
            });
        tracker.Add(new LevelMeasurement(DateTimeOffset.UtcNow, 100, 10, 200));
        var analysis = tracker.Analyze();
        Assert.NotNull(analysis);
        Assert.Equal(GrowthPhase.Collecting, analysis!.Phase);
    }
}
