using SourdoughMonitor.Analysis;
using SourdoughMonitor.Mqtt;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class SwitchableDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    private sealed class Fake(string name, double top, double bottom, double? floor = null) : IJarDetector
    {
        public double Top { get; set; } = top;

        public event Action? SceneChanged;

        public DetectionDiagnostics? LastDiagnostics => null;

        public string LastOutcome { get; private set; } = "no_data";

        public byte[]? LatestAnnotatedImageBytes => null;

        public void RaiseSceneChanged() => SceneChanged?.Invoke();

        public Task<LevelMeasurement?> MeasureAsync(
            byte[] jpegBytes, DateTimeOffset now, double? sessionBaselineHeightPx, CancellationToken ct)
        {
            LastOutcome = name;
            return Task.FromResult<LevelMeasurement?>(new LevelMeasurement(now, Top, 0, bottom, floor));
        }
    }

    private static Task<LevelMeasurement?> Measure(SwitchableDetector detector, int minute) =>
        detector.MeasureAsync([], T0.AddMinutes(minute), null, CancellationToken.None);

    [Fact]
    public async Task OnlyTheActiveDetectorRuns_AndItsOutcomeIsReported()
    {
        var classic = new Fake("classic", 500, 700);
        var llm = new Fake("llm", 500, 710);
        var detector = new SwitchableDetector(classic, llm, null);

        await Measure(detector, 0);
        Assert.Equal("llm", detector.LastOutcome);
        detector.LlmEnabled = false;
        await Measure(detector, 5);
        Assert.Equal("classic", detector.LastOutcome);
        Assert.Equal("llm", llm.LastOutcome);
    }

    [Fact]
    public async Task ClassicDoughFloorIsDropped_SoBothDetectorsShareTheGlassBottomBasis()
    {
        var detector = new SwitchableDetector(new Fake("classic", 500, 700, floor: 680), new Fake("llm", 500, 700), null)
        {
            LlmEnabled = false
        };

        var measurement = await Measure(detector, 0);

        Assert.Null(measurement!.DoughFloorPx);
        Assert.Equal(200, measurement.DoughHeightPx);
    }

    [Fact]
    public async Task AfterASwitch_TheNewDetectorKeepsTheJarBottomTheSessionWasMeasuredWith()
    {
        var classic = new Fake("classic", 500, 690);
        var llm = new Fake("llm", 500, 712);
        var detector = new SwitchableDetector(classic, llm, null);

        var before = await Measure(detector, 0);
        detector.LlmEnabled = false;
        classic.Top = 480; // the dough rose 20 px meanwhile
        var after = await Measure(detector, 5);

        Assert.Equal(712, before!.JarBottomPx);
        Assert.Equal(712, after!.JarBottomPx); // not the classic detector's own 690
        Assert.Equal(before.DoughHeightPx + 20, after.DoughHeightPx);
    }

    [Fact]
    public async Task CameraMove_ClearsTheHeldBottomAndIsForwarded()
    {
        var classic = new Fake("classic", 500, 690);
        var llm = new Fake("llm", 500, 712);
        var detector = new SwitchableDetector(classic, llm, null);
        var moves = 0;
        detector.SceneChanged += () => moves++;
        await Measure(detector, 0);
        detector.LlmEnabled = false;
        await Measure(detector, 5);

        classic.RaiseSceneChanged();
        var after = await Measure(detector, 10);

        Assert.Equal(1, moves);
        Assert.Equal(690, after!.JarBottomPx);
    }

    [Fact]
    public void TheChoiceSurvivesARestart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"detection_mode_{Guid.NewGuid():N}.txt");
        try
        {
            Assert.True(new SwitchableDetector(new Fake("c", 0, 1), new Fake("l", 0, 1), file).LlmEnabled);
            new SwitchableDetector(new Fake("c", 0, 1), new Fake("l", 0, 1), file).LlmEnabled = false;
            Assert.False(new SwitchableDetector(new Fake("c", 0, 1), new Fake("l", 0, 1), file).LlmEnabled);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("ON", true)]
    [InlineData(" off ", false)]
    [InlineData("toggle", null)]
    [InlineData("", null)]
    public void SwitchPayload_IsParsedCaseAndWhitespaceInsensitive(string payload, bool? expected) =>
        Assert.Equal(expected, HaMqttPublisher.ParseSwitchPayload(payload));
}
