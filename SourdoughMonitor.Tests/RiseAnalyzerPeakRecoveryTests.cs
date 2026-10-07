using System.Text.Json.Nodes;

using SourdoughMonitor.Analysis;
using SourdoughMonitor.Config;

namespace SourdoughMonitor.Tests;

/// <summary>Peak recovery retains the feeding session, rejects noise, and survives restart.</summary>
public class RiseAnalyzerPeakRecoveryTests
{
    private static AnalysisOptions NewOptions(string? stateFilePath = null, bool fitEnabled = false) => new()
    {
        SlopeWindowMinutes = 15,
        // These scenarios exercise peak state transitions on noise-free steps, so the
        // rate's uncertainty gate is turned off.
        MaxRateStdErrPercentPerHour = double.MaxValue,
        MinSlopeEvidenceSigmas = 0,
        MinSamplesForFit = fitEnabled ? 8 : 1000,
        PeakConfirmWindows = 3,
        MedianWindowSize = 1,
        MaxRisePxPerMinute = 1000,
        JitterTolerancePx = 6,
        StateFilePath = stateFilePath
    };

    private static LevelMeasurement AtHeight(DateTimeOffset time, double heightPx) =>
        new(time, 1000 - heightPx, 0, 1000);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Analyze_ClearsPeakedOnSustainedRenewedGrowthThenAllowsAFinalPeak(bool fitEnabled)
    {
        var analyzer = new RiseAnalyzer(NewOptions(fitEnabled: fitEnabled));
        var t0 = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);
        var time = t0;
        var readings = new List<RiseReading>();

        LevelMeasurement Next(double heightPx)
        {
            time = time.AddMinutes(5);
            return AtHeight(time, heightPx);
        }

        // Baseline (height 100), not itself a tracked sample.
        var baseline = analyzer.Analyze(AtHeight(t0, 100));
        Assert.True(baseline!.NewSession);

        // Phase A: sustained rise above the minimum needed to identify a peak.
        for (var h = 104; h <= 132; h += 4) readings.Add(analyzer.Analyze(Next(h))!);

        // Phase B: plateau at 132 for long enough to confirm the peak.
        for (var i = 0; i < 8; i++) readings.Add(analyzer.Analyze(Next(132))!);
        Assert.True(readings[^1].Peaked, "sustained flat slope above the rise floor must confirm a peak");

        // Slower renewed growth also exercises fitting against the old plateau.
        var phaseCStart = readings.Count;
        var increment = fitEnabled ? 1 : 4;
        for (var h = 132 + increment; h <= 172; h += increment)
            readings.Add(analyzer.Analyze(Next(h))!);
        Assert.False(readings[^1].Peaked, "sustained renewed growth must clear a stale peak");
        // Once cleared it must stay cleared while growth continues - the fix must not
        // flicker back to Peaked off stale fit/slope state while still climbing.
        var firstUnpeakedIndex = readings.FindIndex(phaseCStart, r => !r.Peaked);
        Assert.True(firstUnpeakedIndex >= phaseCStart, "renewed growth must eventually clear Peaked");
        for (var i = firstUnpeakedIndex; i < readings.Count; i++) Assert.False(readings[i].Peaked);

        // The fix must not achieve this by resetting the session: baseline/start/history
        // from the very first sample are still in force.
        Assert.Equal(100, analyzer.BaselineDoughHeightPx);
        Assert.DoesNotContain(readings, r => r.NewSession);

        // Phase D: a new, genuine final plateau at 172 must still be able to latch Peaked.
        for (var i = 0; i < 8; i++) readings.Add(analyzer.Analyze(Next(172))!);
        Assert.True(readings[^1].Peaked, "a later genuine final peak must still be reported");
    }

    [Fact]
    public void Analyze_DoesNotClearPeakedOnAOneOffSpikeThatImmediatelyReverts()
    {
        var analyzer = new RiseAnalyzer(NewOptions());
        var t0 = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);
        var time = t0;

        LevelMeasurement Next(double heightPx)
        {
            time = time.AddMinutes(5);
            return AtHeight(time, heightPx);
        }

        analyzer.Analyze(AtHeight(t0, 100));
        for (var h = 104; h <= 132; h += 4) analyzer.Analyze(Next(h));
        RiseReading? last = null;
        for (var i = 0; i < 8; i++) last = analyzer.Analyze(Next(132));
        Assert.True(last!.Peaked);

        // One single misdetected/condensation-affected frame spikes up, then the very next
        // frame reverts to the same flat level - this must not be mistaken for sustained
        // renewed growth, however it momentarily skews the rolling slope window.
        last = analyzer.Analyze(Next(140));
        Assert.True(last!.Peaked, "a single spiking frame alone must not clear Peaked");
        for (var i = 0; i < 7; i++)
        {
            last = analyzer.Analyze(Next(132));
            Assert.True(last!.Peaked, "reverting to the flat level must not clear Peaked either");
        }
        // An old accepted spike must not let later sub-jitter drift revoke the peak.
        for (var i = 0; i < 6; i++)
        {
            last = analyzer.Analyze(Next(133));
            Assert.True(last!.Peaked);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Analyze_RestoredPeakedSessionStillClearsOnRenewedGrowthAfterRestart(bool legacyState)
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"sourdough_state_test_{Guid.NewGuid():N}.json");
        try
        {
            var options = NewOptions(stateFile);
            var analyzer = new RiseAnalyzer(options);
            // RestoreState() compares the persisted SessionStart against the real
            // DateTimeOffset.UtcNow, so the fixture's clock must be anchored to now.
            var time = DateTimeOffset.UtcNow;

            LevelMeasurement Next(double heightPx)
            {
                time = time.AddMinutes(5);
                return AtHeight(time, heightPx);
            }

            analyzer.Analyze(AtHeight(time, 100));
            for (var h = 104; h <= 132; h += 4) analyzer.Analyze(Next(h));
            RiseReading? last = null;
            for (var i = 0; i < 8; i++) last = analyzer.Analyze(Next(132));
            Assert.True(last!.Peaked);
            if (legacyState)
            {
                var state = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
                state.Remove("PeakReferenceRisePercent");
                File.WriteAllText(stateFile, state.ToJsonString());
            }

            // Simulate an add-on restart: a fresh instance restores the persisted,
            // already-Peaked session from disk.
            var restored = new RiseAnalyzer(options);

            // Renewed growth continues from where the pre-restart instance left off and
            // must still clear the restored Peaked flag without needing a session reset.
            for (var h = 136; h <= 172; h += 4) last = restored.Analyze(Next(h));
            Assert.False(last!.Peaked, "a restored session must still be able to clear a stale peak");
            Assert.False(last.NewSession);
            Assert.Equal(100, restored.BaselineDoughHeightPx);
        }
        finally
        {
            if (File.Exists(stateFile)) File.Delete(stateFile);
        }
    }
}
