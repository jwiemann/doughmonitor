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
        Assert.NotNull(initial);
        Assert.NotNull(next);
        Assert.True(initial!.NewSession);
        Assert.False(next!.NewSession);
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
        Assert.NotNull(initial);
        // DoughHeightPx = 200 - 120 = 80, below baseline of 100 => -20% clamped to 0
        var next = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), 120, 0, 200));
        Assert.NotNull(next);
        Assert.Equal(0, next!.RisePercent);
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
        Assert.NotNull(initial);
        // DoughHeightPx = 200 - (-500) = 700, baseline = 100 => 600% rise. Spread over 4 hours
        // so the plausibility gate (default 4px/min budget) treats it as a legitimate slow
        // change rather than an implausible jump, exercising the clamp instead of the gate.
        var next = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 4, 0, 0, TimeSpan.Zero), -500, 0, 200));
        Assert.NotNull(next);
        Assert.Equal(500, next!.RisePercent);
    }

    [Fact]
    public void Analyze_RejectsImplausibleJumpAsUnavailable()
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
        Assert.NotNull(initial);
        // Same 600px jump as the clamp test above, but only 5 minutes later: at the default
        // 4px/min + 6px budget that's physically impossible for real dough, so it should be
        // rejected outright (e.g. detection locking onto glare/jar base) instead of reported.
        var next = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), -500, 0, 200));
        Assert.Null(next);
    }

    [Fact]
    public void Analyze_AcceptsASustainedJumpAfterRejectStreakIsExhausted()
    {
        // Models a real handling event (feeding the starter, punching down, folding): the
        // surface moves faster than organic fermentation ever could, but - unlike a one-off
        // misdetected frame - the new height keeps showing up on every subsequent sample
        // instead of reverting. The gate should stop rejecting once that repeats past
        // MaxImplausibleJumpRejects, rather than blacking out data until the time-based
        // budget alone catches up (which could take tens of minutes for a big drop).
        var analyzer = new RiseAnalyzer(
            new AnalysisOptions
            {
                SlopeWindowMinutes = 40,
                ResetDropFraction = 0.25,
                MinSamplesForFit = 3,
                MaxEtaRelativeStdError = 0.15,
                PeakConfirmWindows = 3,
                MaxSessionHours = 36,
                MaxImplausibleJumpRejects = 2,
                StateFilePath = null
            });
        var initial = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 100, 0, 200));
        Assert.NotNull(initial);
        // Same 600px jump repeated every 5 minutes: rejected for the first 2 (the configured
        // streak budget), then accepted on the 3rd instead of staying unavailable.
        var first = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), -500, 0, 200));
        var second = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 10, 0, TimeSpan.Zero), -500, 0, 200));
        var third = analyzer.Analyze(
            new LevelMeasurement(new DateTimeOffset(2024, 1, 1, 0, 15, 0, TimeSpan.Zero), -500, 0, 200));
        Assert.Null(first);
        Assert.Null(second);
        Assert.NotNull(third);
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
            Assert.NotNull(next);
            Assert.False(next!.NewSession);
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

    [Fact]
    public void FindDoughBandTop_RejectsJarBaseShadowUnderAmbientLight()
    {
        // Mirrors a real ambient-lit (non-backlit) jar: bright glass for the first 50 rows,
        // then a step down to a jar-base/table-contact shadow for the last 10 rows. A ~50
        // contrast step like this was observed misidentifying the jar's own base as the dough
        // surface (94% down the jar) when it should have been rejected in favor of the
        // edge-energy fallback.
        var rowIntensity = Enumerable.Repeat(200f, 50)
            .Concat(Enumerable.Repeat(150f, 10))
            .ToArray();
        var bandTop = JarLevelDetector.FindDoughBandTop(rowIntensity, out var contrast);
        Assert.True(contrast < 55);
        Assert.Null(bandTop);
    }

    [Fact]
    public void FindDoughBandTop_StillDetectsGenuineBacklitStep()
    {
        // A true backlit dough band produces a much larger step than a bare ambient-light
        // shadow; this must keep working after raising the acceptance threshold.
        var rowIntensity = Enumerable.Repeat(220f, 50)
            .Concat(Enumerable.Repeat(40f, 10))
            .ToArray();
        var bandTop = JarLevelDetector.FindDoughBandTop(rowIntensity, out var contrast);
        Assert.True(contrast >= 55);
        Assert.NotNull(bandTop);
    }
}
