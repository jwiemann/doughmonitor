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
        var analyzer = new RiseAnalyzer(
            new AnalysisOptions
            {
                SlopeWindowMinutes = 40,
                ResetDropFraction = 0.25,
                MinSamplesForFit = 3,
                MaxEtaRelativeStdError = 0.15,
                PeakConfirmWindows = 3,
                MaxSessionHours = 36,
                StateFilePath = null
            });
        var initial = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 100, 0, 200));
        var next = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), 80, 0, 200));
        Assert.True(initial.NewSession);
        Assert.False(next.NewSession);
        Assert.Equal(20d, next.RisePercent);
    }

    [Fact]
    public void Analyze_ClampsNegativeRisePercentToZero()
    {
        var analyzer = new RiseAnalyzer(
            new AnalysisOptions
            {
                SlopeWindowMinutes = 40,
                ResetDropFraction = 0.25,
                MinSamplesForFit = 3,
                MaxEtaRelativeStdError = 0.15,
                PeakConfirmWindows = 3,
                MaxSessionHours = 36,
                StateFilePath = null
            });
        var initial = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 100, 0, 200));
        // DoughHeightPx = 200 - 120 = 80, below baseline of 100 => -20% clamped to 0
        var next = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), 120, 0, 200));
        Assert.Equal(0, next.RisePercent);
    }

    [Fact]
    public void Analyze_ClampsExtremeHighRisePercentTo500()
    {
        var analyzer = new RiseAnalyzer(
            new AnalysisOptions
            {
                SlopeWindowMinutes = 40,
                ResetDropFraction = 0.25,
                MinSamplesForFit = 3,
                MaxEtaRelativeStdError = 0.15,
                PeakConfirmWindows = 3,
                MaxSessionHours = 36,
                StateFilePath = null
            });
        var initial = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 100, 0, 200));
        // DoughHeightPx = 200 - (-500) = 700, baseline = 100 => 600% clamped to 500
        var next = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), -500, 0, 200));
        Assert.Equal(500, next.RisePercent);
    }

    [Fact]
    public void Analyze_PersistsAndRestoresStateAcrossRestarts()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"sourdough_state_test_{Guid.NewGuid():N}.json");
        try
        {
            var options = new AnalysisOptions
            {
                SlopeWindowMinutes = 40,
                ResetDropFraction = 0.25,
                MinSamplesForFit = 3,
                MaxEtaRelativeStdError = 0.15,
                PeakConfirmWindows = 3,
                MaxSessionHours = 36,
                StateFilePath = stateFile
            };
            var analyzer = new RiseAnalyzer(options);
            // RestoreState() compares the persisted SessionStart against the real
            // DateTimeOffset.UtcNow, so the fixture's clock must be anchored to now.
            var start = DateTimeOffset.UtcNow;
            analyzer.Analyze(new LevelMeasurement(start, 100, 0, 200));
            RiseReading? last = null;
            // Enough samples for a rolling-window slope (>= 4) to be computed and persisted.
            for (var i = 1; i <= 5; i++)
                last = analyzer.Analyze(
                    new LevelMeasurement(start.AddMinutes(5 * i), 100 - 5 * i, 0, 200));
            Assert.NotNull(last);
            Assert.NotNull(last!.RiseRatePercentPerHour);
            Assert.True(File.Exists(stateFile));
            // A fresh instance reading the persisted file must not throw and must recover the session.
            var restored = new RiseAnalyzer(options);
            var next = restored.Analyze(new LevelMeasurement(start.AddMinutes(35), 70, 0, 200));
            Assert.False(next.NewSession);
        }
        finally
        {
            if (File.Exists(stateFile)) File.Delete(stateFile);
        }
    }

    [Fact]
    public void FindDoughSurfaceFromEnergy_PrefersProminentLowerEdge()
    {
        var energy = new[] { 0.8f, 0.8f, 0.8f, 0.1f, 0.1f, 0.1f, 0.1f, 0.95f, 0.95f, 0.95f, 0.95f };
        var surface = JarLevelDetector.FindDoughSurfaceFromEnergy(energy);
        Assert.NotNull(surface);
        Assert.True(surface >= 7);
    }

    [Fact]
    public void FindDoughSurfaceFromEnergy_FallsBackToWeakerLowerBand()
    {
        var energy = new[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.2f, 0.25f, 0.3f, 0.35f, 0.3f, 0.28f, 0.24f, 0.2f };
        var surface = JarLevelDetector.FindDoughSurfaceFromEnergy(energy);
        Assert.NotNull(surface);
        Assert.True(surface >= 7);
    }

    
}
