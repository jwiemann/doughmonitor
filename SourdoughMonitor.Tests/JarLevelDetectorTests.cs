using SourdoughMonitor.Analysis;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class JarLevelDetectorTests
{
    [Fact]
    public void AdjustMeasurementForRoi_AddsVerticalOffsetToAllCoordinates()
    {
        var measurement = new LevelMeasurement(DateTimeOffset.Now, 20, 10, 30);
        var adjusted = JarLevelDetector.AdjustMeasurementForRoi(measurement, 15);
        Assert.Equal(45, adjusted.JarBottomPx);
        Assert.Equal(25, adjusted.JarTopPx);
        Assert.Equal(35, adjusted.DoughTopPx);
    }

    
}
