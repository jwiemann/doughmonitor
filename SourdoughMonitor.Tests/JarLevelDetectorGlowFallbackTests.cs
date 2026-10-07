using OpenCvSharp;

using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

/// <summary>Real frames from the 2026-10-06 bake: the LED glow sits directly above the rising
/// dough surface for ~105 minutes, which used to blind the detector (no_surface) and pinch
/// the reported jar column to half its established width.</summary>
public class JarLevelDetectorGlowFallbackTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 17, 0, 0, TimeSpan.Zero);

    private static JarLevelDetector NewDetector() => new(new VisionOptions
    {
        GeometryStateFilePath = null,
        DebugSaveAnnotatedImages = false
    });

    private static byte[] Frame(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // The real frames are 27-43 minutes apart; replay time is condensed so every gap stays
    // inside the 30 minute continuity window the minute-by-minute live feed would provide.
    private static readonly (string File, int Minutes, double Top)[] Sequence =
    [
        ("glow-parity-1700.jpg", 0, 272),
        ("glow-blind-1703.jpg", 3, 268),
        ("glow-blind-1730.jpg", 30, 240),
        ("glow-blind-1800.jpg", 58, 208),
        ("glow-blind-1843.jpg", 84, 180),
    ];

    [Fact]
    public void GlowAboveSurface_TracksRisingSurfaceOnEstablishedColumn()
    {
        var detector = NewDetector();
        foreach (var (file, minutes, top) in Sequence)
        {
            var measured = detector.Measure(Frame(file), Noon.AddMinutes(minutes));
            Assert.True(measured is not null, $"{file} lost ({detector.LastOutcome})");
            Assert.InRange(measured!.DoughTopPx, top - 10, top + 10);
            Assert.InRange(detector.LastDiagnostics!.JarLeftPx!.Value, 320, 360);
            Assert.InRange(detector.LastDiagnostics.JarRightPx!.Value, 940, 985);
        }
    }

    [Fact]
    public void StaleLastSurface_DoesNotAnchorTheContinuitySearch()
    {
        var detector = NewDetector();
        Assert.NotNull(detector.Measure(Frame("glow-parity-1700.jpg"), Noon));
        Assert.Null(detector.Measure(Frame("glow-blind-1730.jpg"), Noon.AddMinutes(40)));
        Assert.Equal("no_surface", detector.LastOutcome);
    }

    [Fact]
    public void ParityFrames_KeepTheirExistingLevels()
    {
        var evening = NewDetector().Measure(Frame("glow-parity-1700.jpg"), Noon);
        Assert.NotNull(evening);
        Assert.InRange(evening!.DoughTopPx, 266, 278);
        var night = NewDetector().Measure(Frame("glow-parity-2100.jpg"), Noon.AddHours(4));
        Assert.NotNull(night);
        Assert.InRange(night!.DoughTopPx, 567, 583);
    }

    [Theory]
    [InlineData("glow-empty-1851.jpg")]
    [InlineData("glow-empty-1905.jpg")]
    [InlineData("glow-empty-1912.jpg")]
    public void EmptyJarFrames_NeverInventDoughAfterSurfaceContinuity(string file)
    {
        var detector = NewDetector();
        foreach (var (known, minutes, _) in Sequence)
            Assert.NotNull(detector.Measure(Frame(known), Noon.AddMinutes(minutes)));
        var measured = detector.Measure(Frame(file), Noon.AddMinutes(88));
        if (measured is not null)
            Assert.InRange(measured.DoughTopPx, 570, 586);
    }

    [Fact]
    public void TotalFailureWithWidthDisagreement_ReplacesEstablishedColumn()
    {
        // The real total-failure case, replayed from the 2026-10-06 blind window: with the
        // continuity fallback disabled these frames yield nothing on the established column
        // while the candidate is the glow-blob pinch (~317 px). The relief valve must then
        // hand the candidate to move validation instead of holding the stale width forever.
        // (While a fallback keeps measuring, the established column stays authoritative —
        // covered by GlowAboveSurface_TracksRisingSurfaceOnEstablishedColumn.)
        var reliefFrames = new VisionOptions().ColumnWidthReliefFrames;
        var detector = new JarLevelDetector(new VisionOptions
        {
            GeometryStateFilePath = null,
            DebugSaveAnnotatedImages = false,
            SurfaceContinuityMinutes = 0
        });
        Assert.NotNull(detector.Measure(Frame("glow-parity-1700.jpg"), Noon));
        var widths = new List<int>();
        for (var i = 1; i <= reliefFrames + 6; i++)
        {
            Assert.Null(detector.Measure(Frame("glow-blind-1730.jpg"), Noon.AddMinutes(i)));
            widths.Add(detector.LastDiagnostics!.JarRightPx!.Value - detector.LastDiagnostics.JarLeftPx!.Value);
        }
        // The disagreeing width is held off for ColumnWidthReliefFrames - 1 frames ...
        Assert.All(widths.Take(reliefFrames - 1), w => Assert.InRange(w, 590, 610));
        // ... then it is validated as a move and the new width takes over.
        Assert.InRange(widths[reliefFrames - 1], 295, 345);
        Assert.InRange(widths[^1], 295, 345);
    }
}
