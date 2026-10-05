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
    public void Analyze_ExposesBaselineHeightAndSessionStartForDebugOverlayAndMqtt()
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
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(analyzer.BaselineDoughHeightPx);
        var initial = analyzer.Analyze(new LevelMeasurement(start, 100, 0, 200));
        Assert.NotNull(initial);
        // DoughHeightPx = 200 - 100 = 100 becomes both the baseline used for rise% and the
        // session's recorded start time.
        Assert.Equal(100d, analyzer.BaselineDoughHeightPx);
        Assert.Equal(start, initial!.SessionStart);
        var next = analyzer.Analyze(new LevelMeasurement(start.AddMinutes(5), 80, 0, 200));
        Assert.NotNull(next);
        // A later sample within the same session keeps reporting the original start time,
        // not the current sample's time.
        Assert.Equal(start, next!.SessionStart);
        Assert.Equal(100d, analyzer.BaselineDoughHeightPx);
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
    public void Analyze_DoesNotResetSessionOnASingleCollapseLookingSample()
    {
        // Models a jar reappearing after a detection gap: the vision pipeline hasn't
        // reacquired the true surface yet and reports one low frame, then recovers to the
        // established level on the very next sample. This must not wipe the session.
        // MedianWindowSize = 1 isolates the collapse-confirmation logic from the separate
        // smoothing window's own damping behavior.
        var analyzer = new RiseAnalyzer(
            new AnalysisOptions
            {
                SlopeWindowMinutes = 40,
                ResetDropFraction = 0.25,
                MinSamplesForFit = 1000,
                MaxEtaRelativeStdError = 0.15,
                PeakConfirmWindows = 3,
                MaxSessionHours = 36,
                MedianWindowSize = 1,
                CollapseConfirmSamples = 3,
                StateFilePath = null
            });
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Baseline: DoughHeightPx = 200 - 100 = 100.
        analyzer.Analyze(new LevelMeasurement(start, 100, 0, 200));
        // Establish a real 50% rise (DoughHeightPx = 150) over several samples, 30 minutes
        // apart so each jump stays within the plausibility gate's rate budget, so
        // recentMedian > 20 once the collapse check is exercised below.
        for (var i = 1; i <= 5; i++)
            analyzer.Analyze(new LevelMeasurement(start.AddMinutes(30 * i), 50, 0, 200));
        // One low frame (DoughHeightPx = 90, an apparent drop below the 37.5% collapse
        // threshold), immediately followed by recovery back to the established level: the
        // drop must be treated as unavailable, not a session reset.
        var dropped = analyzer.Analyze(new LevelMeasurement(start.AddMinutes(180), 110, 0, 200));
        var recovered = analyzer.Analyze(new LevelMeasurement(start.AddMinutes(210), 55, 0, 200));
        Assert.Null(dropped);
        Assert.NotNull(recovered);
        Assert.False(recovered!.NewSession);
    }

    [Fact]
    public void Analyze_ResetsSessionWhenCollapseIsConfirmedAcrossSamples()
    {
        // A genuine collapse (punch-down, deflating starter) keeps reporting the lower
        // level instead of reverting; once it persists for CollapseConfirmSamples in a row
        // the session should reset.
        var analyzer = new RiseAnalyzer(
            new AnalysisOptions
            {
                SlopeWindowMinutes = 40,
                ResetDropFraction = 0.25,
                MinSamplesForFit = 1000,
                MaxEtaRelativeStdError = 0.15,
                PeakConfirmWindows = 3,
                MaxSessionHours = 36,
                MedianWindowSize = 1,
                CollapseConfirmSamples = 3,
                StateFilePath = null
            });
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        analyzer.Analyze(new LevelMeasurement(start, 100, 0, 200));
        for (var i = 1; i <= 5; i++)
            analyzer.Analyze(new LevelMeasurement(start.AddMinutes(30 * i), 50, 0, 200));
        // The dough surface drops (DoughHeightPx = 90) and stays dropped on every
        // subsequent sample, 30 minutes apart.
        var first = analyzer.Analyze(new LevelMeasurement(start.AddMinutes(180), 110, 0, 200));
        var second = analyzer.Analyze(new LevelMeasurement(start.AddMinutes(210), 110, 0, 200));
        var third = analyzer.Analyze(new LevelMeasurement(start.AddMinutes(240), 110, 0, 200));
        Assert.Null(first);
        Assert.Null(second);
        Assert.NotNull(third);
        Assert.True(third!.NewSession);
    }

    [Fact]
    public void Analyze_PersistsAndRestoresStateAcrossRestarts()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"sourdough_state_test_{Guid.NewGuid():N}.json");
        try
        {
            var options = new AnalysisOptions
            {
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
            // A rate needs a span long enough to be known within the standard-error limit;
            // the first samples also still carry the median window's warm-up lag.
            for (var i = 1; i <= 20; i++)
                last = analyzer.Analyze(
                    new LevelMeasurement(start.AddMinutes(5 * i), 100 - 5 * i, 0, 200));
            Assert.NotNull(last);
            Assert.NotNull(last!.RiseRatePercentPerHour);
            // A restart must retain the same feeding session and calculated trend.
            var restored = new RiseAnalyzer(options);
            var next = restored.Analyze(new LevelMeasurement(start.AddMinutes(105), 100 - 105, 0, 200));
            Assert.NotNull(next);
            Assert.False(next!.NewSession);
            Assert.Equal(start, next.SessionStart);
            Assert.Equal(100, restored.BaselineDoughHeightPx);
            Assert.NotNull(next.RiseRatePercentPerHour);
        }
        finally
        {
            if (File.Exists(stateFile)) File.Delete(stateFile);
        }
    }


    [Fact]
    public void Analyze_ResetsSessionWhenDoughDropsBelowBaselineAfterRefeed()
    {
        var analyzer = NewAnalyzer();
        var start = new DateTimeOffset(2026, 10, 1, 7, 15, 0, TimeSpan.Zero);
        analyzer.Analyze(new LevelMeasurement(start, 100, 0, 200));
        RiseReading? reset = null;
        // Dough top drops to 140 (height 60 vs baseline 100) and stays there.
        for (var i = 1; i <= 8 && reset is null; i++)
        {
            var reading = analyzer.Analyze(new LevelMeasurement(start.AddMinutes(5 * i), 140, 0, 200));
            if (reading?.NewSession == true) reset = reading;
        }
        Assert.NotNull(reset);
        Assert.Null(reset!.RiseRatePercentPerHour);
        Assert.Equal(60, analyzer.BaselineDoughHeightPx);
        // The next frame measures against the new baseline (height 60).
        var next = analyzer.Analyze(new LevelMeasurement(start.AddMinutes(20), 135, 0, 200));
        Assert.NotNull(next);
        Assert.False(next!.NewSession);
        Assert.Equal(8.3, next.RisePercent, precision: 1);
    }

    [Fact]
    public void Analyze_PublishesRiseRateOnlyOnceItsStandardErrorIsSmall()
    {
        var start = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

        // A few minutes of post-reset jitter used to be extrapolated to an hourly slope
        // and published as an absurd -100 %/h. Its standard error is far too large.
        var jittery = NewAnalyzer();
        jittery.Analyze(AtHeight(start, 100));
        for (var minute = 1; minute <= 8; minute++)
        {
            var reading = jittery.Analyze(AtHeight(start.AddMinutes(minute), 112 - 2 * minute));
            Assert.Null(reading?.RiseRatePercentPerHour);
        }

        // A clean gradual decline is published as soon as the span pins the slope to
        // within the limit (about 35 minutes of 1-minute samples on a 100 px baseline).
        var declining = NewAnalyzer();
        declining.Analyze(AtHeight(start, 100));
        int? firstMinute = null;
        RiseReading? latest = null;
        for (var minute = 5; minute <= 60; minute++)
        {
            latest = declining.Analyze(AtHeight(start.AddMinutes(minute), 120 - 0.1 * minute));
            if (latest?.RiseRatePercentPerHour is not null) firstMinute ??= minute;
        }
        Assert.InRange(firstMinute!.Value, 30, 45);
        Assert.InRange(latest!.RiseRatePercentPerHour!.Value, -7, -5);

        // The trend is fitted on the true rise, not on its display clamp at 0 %, so the
        // rate stays correct while the dough sits below its session baseline.
        var belowBaseline = NewAnalyzer();
        belowBaseline.Analyze(AtHeight(start, 100));
        RiseReading? below = null;
        for (var minute = 1; minute <= 60; minute++)
            below = belowBaseline.Analyze(AtHeight(start.AddMinutes(minute), 104 - 0.1 * minute));
        Assert.Equal(0, below!.RisePercent);
        Assert.InRange(below.RiseRatePercentPerHour!.Value, -7, -5);
    }

    [Fact]
    public void Analyze_ConfirmsFeedingDropOnRawHeightsInsteadOfTheBlendedMedian()
    {
        // The 5-sample median still shows the old plateau for the first drop frames, so a
        // reset confirmed on it blends the discontinuity into the new session's slope.
        var analyzer = NewAnalyzer();
        var start = new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        analyzer.Analyze(AtHeight(start, 100));
        for (var minute = 5; minute <= 60; minute += 5)
            analyzer.Analyze(AtHeight(start.AddMinutes(minute), 100 + minute * 0.8));
        for (var minute = 65; minute <= 100; minute += 5)
            analyzer.Analyze(AtHeight(start.AddMinutes(minute), 148));
        Assert.Null(analyzer.Analyze(AtHeight(start.AddMinutes(105), 91)));
        Assert.Null(analyzer.Analyze(AtHeight(start.AddMinutes(110), 93)));
        var reset = analyzer.Analyze(AtHeight(start.AddMinutes(115), 92));
        Assert.True(reset!.NewSession);
        Assert.Null(reset.RiseRatePercentPerHour);
        Assert.Equal(92, analyzer.BaselineDoughHeightPx);
    }

    [Fact]
    public void Analyze_FlatLagPhaseNeverFlagsPeaked()
    {
        // Degenerate-fit case observed live: a flat 6.6% lag phase fits a sigmoid with
        // plateau ≈ current value perfectly — the "Peaked" sensor must stay off.
        var analyzer = NewAnalyzer();
        var t0 = new DateTimeOffset(2026, 10, 1, 7, 15, 0, TimeSpan.Zero);
        analyzer.Analyze(new LevelMeasurement(t0, 100, 0, 200));
        var readings = new List<RiseReading>();
        for (var i = 1; i <= 14; i++)
        {
            readings.Add(analyzer.Analyze(new LevelMeasurement(t0.AddMinutes(5 * i), 93.4, 0, 200)));
        }
        Assert.All(readings, r => Assert.False(r.Peaked));
    }

    private static RiseAnalyzer NewAnalyzer() => new(new AnalysisOptions
    {
        SlopeWindowMinutes = 40,
        ResetDropFraction = 0.25,
        MinSamplesForFit = 3,
        MaxEtaRelativeStdError = 0.15,
        PeakConfirmWindows = 3,
        MaxSessionHours = 36,
        StateFilePath = null
    });

    private static LevelMeasurement AtHeight(DateTimeOffset time, double heightPx) =>
        new(time, 1000 - heightPx, 0, 1000);
}
