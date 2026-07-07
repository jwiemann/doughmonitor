using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;
using Xunit;

namespace SourdoughMonitor.Tests;

public class RiseAnalyzerTests
{
    [Fact]
    public void Analyze_TracksPositiveRiseAfterBaselineIsSet()
    {
        var analyzer = new RiseAnalyzer(new AnalysisOptions
        {
            SlopeWindowMinutes = 40,
            ResetDropFraction = 0.25,
            MinSamplesForFit = 3,
            MaxEtaRelativeStdError = 0.15,
            PeakConfirmWindows = 3,
            MaxSessionHours = 36,
            StateFilePath = null
        });

        var initial = analyzer.Analyze(new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 100, 0, 200));
        var next = analyzer.Analyze(new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), 80, 0, 200));

        Assert.True(initial.NewSession);
        Assert.False(next.NewSession);
        Assert.Equal(20d, next.RisePercent);
    }

    [Fact]
    public void FindDoughSurfaceFromEnergy_PrefersProminentLowerEdge()
    {
        var energy = new[]
        {
            0.8f, 0.8f, 0.8f,
            0.1f, 0.1f, 0.1f, 0.1f,
            0.95f, 0.95f, 0.95f, 0.95f
        };

        var surface = JarLevelDetector.FindDoughSurfaceFromEnergy(energy);

        Assert.NotNull(surface);
        Assert.True(surface >= 7);
    }

    [Fact]
    public void FindDoughSurfaceFromEnergy_FallsBackToWeakerLowerBand()
    {
        var energy = new[]
        {
            0.1f, 0.1f, 0.1f, 0.1f,
            0.2f, 0.25f, 0.3f, 0.35f,
            0.3f, 0.28f, 0.24f, 0.2f
        };

        var surface = JarLevelDetector.FindDoughSurfaceFromEnergy(energy);

        Assert.NotNull(surface);
        Assert.True(surface >= 7);
    }

    [Fact]
    public void FindDoughSurfaceCombined_PicksTheMainBrightToDarkTransition()
    {
        var intensity = new[]
        {
            95f, 95f, 95f, 95f, 95f, 95f, 95f, 95f,
            70f, 65f, 60f, 55f, 50f, 40f, 30f, 25f, 20f, 20f, 20f, 20f
        };

        var surface = JarLevelDetector.FindDoughSurfaceCombined(new float[intensity.Length], intensity);

        Assert.NotNull(surface);
        Assert.InRange(surface.Value, 7, 9);
    }
}
