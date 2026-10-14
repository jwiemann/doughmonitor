using System.Globalization;

using Microsoft.Extensions.Logging.Abstractions;

using OpenCvSharp;

using SourdoughMonitor.Config;
using SourdoughMonitor.Analysis;
using SourdoughMonitor.Services;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class LlmJarDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 3, 0, 0, TimeSpan.Zero);

    private static readonly LlmOptions Llm = new() { SamplesPerCycle = 1 };

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Annotation JSON for a jar spanning x 300..700, y 100..900 (grid units) shifted
    /// by <paramref name="dx"/>, with a flat front surface at <paramref name="surfaceY"/>.</summary>
    private static string Json(double surfaceY, double dx = 0, double confidence = 0.9, bool visible = true,
        string? surface = null)
    {
        var l = 300 + dx;
        var r = 700 + dx;
        var outline =
            $"[[{F(l)},100],[{F(l)},500],[{F(l)},900],[{F(500 + dx)},900],[{F(r)},900],[{F(r)},500],[{F(r)},100]]";
        surface ??= $"[[{F(l + 20)},{F(surfaceY)}],[{F(500 + dx)},{F(surfaceY)}],[{F(r - 20)},{F(surfaceY)}]]";
        return $"{{\"jar_visible\":{(visible ? "true" : "false")},\"jar_outline\":{outline},"
               + $"\"dough_surface\":{surface},\"confidence\":{F(confidence)}}}";
    }

    private static LlmInterpretation Interpret(string json, (int, int, int, int)? crop = null)
    {
        var annotation = LlmAnnotationInterpreter.Parse(json, out var error);
        Assert.True(annotation is not null, error);
        return LlmAnnotationInterpreter.Interpret(annotation!, crop ?? (0, 0, 1000, 1000), Llm);
    }

    [Fact]
    public void Level_IsMeanOfCentralSixtyPercent_IgnoringMeniscusClimbAtTheWalls()
    {
        // Ends of the curve climb the glass (y 450); the central 60% sits at 500.
        const string surface = "[[300,450],[380,500],[620,500],[700,450]]";
        var result = Interpret(Json(0, surface: surface), crop: (0, 100, 1000, 500));

        Assert.Equal("detected", result.Outcome);
        Assert.Equal(100 + 0.5 * 500, result.SurfaceRow!.Value, 6);
    }

    [Fact]
    public void Parse_AcceptsCodeFencesAndProse()
    {
        var annotation = LlmAnnotationInterpreter.Parse($"Here you go:\n```json\n{Json(500)}\n```", out var error);

        Assert.Null(error);
        Assert.Equal(7, annotation!.JarOutline.Count);
    }

    [Theory]
    [InlineData("not json at all", "no_json")]
    [InlineData("{\"jar_visible\":true,\"jar_outline\":[[1]],\"dough_surface\":[],\"confidence\":1}", "bad_points")]
    public void Parse_RejectsMalformedAnswers(string content, string expected)
    {
        Assert.Null(LlmAnnotationInterpreter.Parse(content, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData(0.1, true, "[[320,500],[500,500],[680,500]]", "llm_rejected", "low_confidence")]
    [InlineData(0.9, false, "[[320,500],[500,500],[680,500]]", "no_jar", "jar_not_visible")]
    [InlineData(0.9, true, "[]", "no_surface", "empty_surface")]
    [InlineData(0.9, true, "[[320,500]]", "llm_rejected", "surface_points")]
    [InlineData(0.9, true, "[[320,500],[400,500]]", "llm_rejected", "surface_span")]
    [InlineData(0.9, true, "[[100,500],[500,500],[900,500]]", "llm_rejected", "surface_outside_jar")]
    [InlineData(0.9, true, "[[320,890],[500,890],[680,890]]", "no_surface", "surface_at_bottom")]
    public void Interpret_ClassifiesUnusableAnswers(
        double confidence, bool visible, string surface, string outcome, string reason)
    {
        var result = Interpret(Json(0, confidence: confidence, visible: visible, surface: surface));

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Interpret_RejectsOutlineThatIsNotJarShaped()
    {
        const string flat = "{\"jar_visible\":true,\"jar_outline\":[[100,500],[900,500],[900,550],[100,550]],"
                            + "\"dough_surface\":[[200,520],[800,520]],\"confidence\":0.9}";

        Assert.Equal("jar_shape", Interpret(flat).Reason);
    }

    private sealed class FakeClient(Queue<string> answers) : IVisionLlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmCompletion(answers.Dequeue(), 100, 20, 0.0001));
        }
    }

    private static byte[] Frame(byte background, byte bright)
    {
        using var img = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(background, background, background));
        Cv2.Rectangle(img, new Rect(400, 100, 480, 560), new Scalar(bright, bright, bright), -1);
        Cv2.ImEncode(".jpg", img, out var bytes);
        return bytes;
    }

    private static LlmJarDetector Detector(FakeClient client) => new(
        new VisionOptions { DebugSaveAnnotatedImages = false }, Llm, client, NullLogger<LlmJarDetector>.Instance);

    [Fact]
    public async Task DarkFrame_IsGatedWithoutCallingTheModel()
    {
        var client = new FakeClient(new Queue<string>());
        var detector = Detector(client);

        var measurement = await detector.MeasureAsync(Frame(3, 8), T0, null, CancellationToken.None);

        Assert.Null(measurement);
        Assert.Equal("dark_frame", detector.LastOutcome);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Measurement_UsesFullFramePixelsAndMedianJarBottom()
    {
        var client = new FakeClient(new Queue<string>([Json(500), Json(500), Json(500)]));
        var detector = Detector(client);
        LevelMeasurement? last = null;

        for (var i = 0; i < 3; i++)
            last = await detector.MeasureAsync(Frame(40, 200), T0.AddMinutes(i), null, CancellationToken.None);

        Assert.Equal("detected", detector.LastOutcome);
        Assert.Equal(0.5 * 720, last!.DoughTopPx, 6);
        Assert.Equal(0.9 * 720, last.JarBottomPx, 6);
        Assert.Equal(0.1 * 720, last.JarTopPx, 6);
        Assert.Null(last.DoughFloorPx);
    }

    [Fact]
    public async Task OneOddOutline_DoesNotRejectTheFrameOrMoveTheMedianJar()
    {
        var answers = Enumerable.Repeat(Json(500), 10).Append(Json(500, dx: 150));
        var detector = Detector(new FakeClient(new Queue<string>(answers)));
        var sceneChanges = 0;
        detector.SceneChanged += () => sceneChanges++;
        for (var i = 0; i < 10; i++)
            await detector.MeasureAsync(Frame(40, 200), T0.AddMinutes(i), null, CancellationToken.None);

        var odd = await detector.MeasureAsync(Frame(40, 200), T0.AddMinutes(10), null, CancellationToken.None);

        Assert.NotNull(odd);
        Assert.Equal(0.9 * 720, odd!.JarBottomPx, 6);
        Assert.Equal(0, sceneChanges);
    }

    [Fact]
    public async Task SustainedOutlineShift_RaisesSceneChangeOnceMostRecentFramesAgree()
    {
        var answers = Enumerable.Repeat(Json(500), 10).Concat(Enumerable.Repeat(Json(500, dx: 150), 5));
        var detector = Detector(new FakeClient(new Queue<string>(answers)));
        var sceneChanges = 0;
        detector.SceneChanged += () => sceneChanges++;
        for (var i = 0; i < 10; i++)
            await detector.MeasureAsync(Frame(40, 200), T0.AddMinutes(i), null, CancellationToken.None);

        for (var i = 10; i < 12; i++)
            await detector.MeasureAsync(Frame(40, 200), T0.AddMinutes(i), null, CancellationToken.None);
        Assert.Equal(0, sceneChanges);

        for (var i = 12; i < 15; i++)
            await detector.MeasureAsync(Frame(40, 200), T0.AddMinutes(i), null, CancellationToken.None);

        Assert.Equal(1, sceneChanges);
    }

    private static readonly LlmOptions ThreeSamples = new() { SamplesPerCycle = 3 };

    private static LlmJarDetector Sampling(params string[] answers) => new(
        new VisionOptions { DebugSaveAnnotatedImages = false },
        ThreeSamples,
        new FakeClient(new Queue<string>(answers)),
        NullLogger<LlmJarDetector>.Instance);

    [Fact]
    public async Task ThreeSamples_UseTheMedianLevelAndRecordTheSpread()
    {
        var detector = Sampling(Json(480), Json(560), Json(500));

        var measurement = await detector.MeasureAsync(Frame(40, 200), T0, null, CancellationToken.None);

        Assert.Equal(0.5 * 720, measurement!.DoughTopPx, 6);
        Assert.Equal(80 * 0.72, detector.LastDiagnostics!.SampleSpreadPx!.Value, 6);
        Assert.Equal(3, detector.LastDiagnostics.SampleCount);
        Assert.Equal(1, detector.Spread.Cycles);
        Assert.Equal(1, detector.Spread.Noisy);
    }

    [Fact]
    public async Task OneFailedSample_StillMeasuresFromTheMajority()
    {
        var detector = Sampling(Json(500), Json(0, visible: false), Json(520));

        var measurement = await detector.MeasureAsync(Frame(40, 200), T0, null, CancellationToken.None);

        Assert.NotNull(measurement);
        Assert.Equal(0.51 * 720, measurement!.DoughTopPx, 6);
        Assert.Equal(2, detector.LastDiagnostics!.SampleCount);
    }

    [Fact]
    public async Task MinorityOfUsableSamples_ReportsTheCommonRejection()
    {
        var detector = Sampling(Json(500), Json(0, visible: false), Json(0, visible: false));

        var measurement = await detector.MeasureAsync(Frame(40, 200), T0, null, CancellationToken.None);

        Assert.Null(measurement);
        Assert.Equal("no_jar", detector.LastOutcome);
    }
}
