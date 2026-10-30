using SourdoughMonitor.Config;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

/// <summary>Real IR night frames from the 2026-10-29/30 bread proof: a lamp behind the jar, the dough a
/// dark mass between the lamp glow and the saturated glass foot. The glow path used to pick the foot
/// as the light source and lose the surface; the stale continuity fallback then latched onto the
/// glass base (row ~597) and the rise stayed at 0 % for eight hours.</summary>
public class JarLevelDetectorDarkMassTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 22, 3, 0, TimeSpan.Zero);

    private static byte[] Frame(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void DarkDoughAboveLitFoot_IsMeasuredAtTheSurfaceNotTheGlassBase()
    {
        var detector = new JarLevelDetector(new VisionOptions { GeometryStateFilePath = null, DebugSaveAnnotatedImages = false });
        var first = detector.Measure(Frame("darkmass-night-2203.jpg"), Start);
        Assert.True(first is not null, $"first frame lost ({detector.LastOutcome})");
        Assert.Equal("dark_mass", detector.LastDiagnostics!.Method);
        Assert.InRange(first!.DoughTopPx, 424, 434);

        var later = detector.Measure(Frame("darkmass-night-0608.jpg"), Start.AddHours(8));
        Assert.True(later is not null, $"later frame lost ({detector.LastOutcome})");
        Assert.InRange(later!.DoughTopPx, 405, 415);
        Assert.InRange(first.DoughHeightPx - later.DoughHeightPx, -26, -14);
        Assert.InRange(later.DoughFloorPx!.Value, 548, 558);
    }
}
