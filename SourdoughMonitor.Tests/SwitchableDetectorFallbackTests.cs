using SourdoughMonitor.Analysis;
using SourdoughMonitor.Models;
using SourdoughMonitor.Vision;
using Xunit;

namespace SourdoughMonitor.Tests;

public class SwitchableDetectorFallbackTests
{
    private sealed class Fake(string outcome, params LevelMeasurement?[] results) : IJarDetector
    {
        private int _i;
        public int Calls { get; private set; }
        public string Outcome { get; set; } = outcome;
        public event Action? SceneChanged { add { } remove { } }
        public DetectionDiagnostics? LastDiagnostics => null;
        public string LastOutcome => Outcome;
        public byte[]? LatestAnnotatedImageBytes => null;
        public Task<LevelMeasurement?> MeasureAsync(byte[] jpegBytes, DateTimeOffset now, double? b, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(results[Math.Min(_i++, results.Length - 1)]);
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static LevelMeasurement M(double top, int minute = 0) => new(T0.AddMinutes(minute), top, 20, 700);

    private static SwitchableDetector Detector(Fake classic, Fake llm) => new(classic, llm, null, llmDefault: false);

    [Fact]
    public async Task ClassicMissesTheSurface_TheModelPlacesIt()
    {
        var classic = new Fake("no_surface", (LevelMeasurement?)null);
        var llm = new Fake("detected", M(320));
        var result = await Detector(classic, llm).MeasureAsync([], T0, null, default);
        Assert.Equal(320, result!.DoughTopPx);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task DarkFrame_IsNotEscalated()
    {
        var classic = new Fake("dark_frame", (LevelMeasurement?)null);
        var llm = new Fake("detected", M(320));
        Assert.Null(await Detector(classic, llm).MeasureAsync([], T0, null, default));
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task ImplausibleJump_TheModelDecides_PlausibleStepIsKept()
    {
        var classic = new Fake("detected", M(400), M(398, 5), M(330, 10));
        var llm = new Fake("detected", M(396, 10));
        var detector = Detector(classic, llm);
        await detector.MeasureAsync([], T0, null, default);
        Assert.Equal(398, (await detector.MeasureAsync([], T0.AddMinutes(5), null, default))!.DoughTopPx);
        Assert.Equal(0, llm.Calls);
        Assert.Equal(396, (await detector.MeasureAsync([], T0.AddMinutes(10), null, default))!.DoughTopPx);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task FallbackOff_NeverCallsTheModel()
    {
        var classic = new Fake("no_surface", (LevelMeasurement?)null);
        var llm = new Fake("detected", M(320));
        var detector = Detector(classic, llm);
        detector.LlmFallback = false;
        Assert.Null(await detector.MeasureAsync([], T0, null, default));
        Assert.Equal(0, llm.Calls);
    }
}
