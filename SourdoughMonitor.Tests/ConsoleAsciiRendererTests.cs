using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SourdoughMonitor.Analysis;
using SourdoughMonitor.Services;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class ConsoleAsciiRendererTests
{
    [Fact]
    public void RenderAscii_ReturnsTextArtForImageBytes()
    {
        using var image = new Image<Rgba32>(12, 6);
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        var renderer = new AsciiConsoleRenderer();
        var art = renderer.RenderAscii(stream.ToArray(), width: 20, height: 8);

        Assert.False(string.IsNullOrWhiteSpace(art));
        Assert.Contains('\n', art);
    }

    [Fact]
    public void BuildDisplayText_IncludesHeaderAndRenderedArt()
    {
        using var image = new Image<Rgba32>(12, 6);
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        var renderer = new AsciiConsoleRenderer();
        var display = renderer.BuildDisplayText(stream.ToArray(), width: 20, height: 8, status: "frame ok");

        Assert.Contains("preview", display);
        Assert.Contains("frame ok", display);
        Assert.Contains('\n', display);
    }

    [Fact]
    public void BuildDisplayText_IncludesGrowthSummaryWhenProvided()
    {
        using var image = new Image<Rgba32>(12, 6);
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        var renderer = new AsciiConsoleRenderer();
        renderer.SetGrowthAnalysis(new GrowthAnalysis(20, 10, 1.5, 12.5, 0.5, RiseTrend.Accelerating, null, null, DateTimeOffset.Now.AddHours(4), GrowthPhase.Rising));
        var display = renderer.BuildDisplayText(stream.ToArray(), width: 20, height: 8, status: "frame ok");

        Assert.Contains("growth", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rising", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDisplayText_IncludesGrowthInMeasurementLineWhenProvided()
    {
        using var image = new Image<Rgba32>(12, 6);
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        var renderer = new AsciiConsoleRenderer();
        var measurement = new LevelMeasurement(DateTimeOffset.Now, 20, 10, 30);
        renderer.SetGrowthAnalysis(new GrowthAnalysis(20, 10, 1.5, 12.5, 0.5, RiseTrend.Accelerating, DateTimeOffset.Now.AddHours(6), null, DateTimeOffset.Now.AddHours(4), GrowthPhase.Rising));
        var display = renderer.BuildDisplayText(stream.ToArray(), width: 20, height: 8, status: "frame ok", measurement);

        Assert.Contains("jar", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("growth", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDisplayText_ShowsTrendArrowAndTargetOrPeakHints()
    {
        using var image = new Image<Rgba32>(12, 6);
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        var renderer = new AsciiConsoleRenderer();
        renderer.SetGrowthAnalysis(new GrowthAnalysis(20, 10, 1.5, 12.5, 0.5, RiseTrend.Accelerating, DateTimeOffset.Now.AddHours(6), null, DateTimeOffset.Now.AddHours(4), GrowthPhase.Rising));
        var display = renderer.BuildDisplayText(stream.ToArray(), width: 20, height: 8, status: "frame ok");

        Assert.Contains("↑", display, StringComparison.Ordinal);
        Assert.Contains("target", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("peak", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDisplayText_IncludesDetectorDiagnosticsWhenProvided()
    {
        using var image = new Image<Rgba32>(12, 6);
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);

        var renderer = new AsciiConsoleRenderer();
        var measurement = new LevelMeasurement(DateTimeOffset.Now, 20, 10, 30);
        var diagnostics = new JarLevelDetector.DetectionDiagnostics("band", 87.5, 12, 20);
        var display = renderer.BuildDisplayText(stream.ToArray(), width: 20, height: 8, status: "frame ok", measurement, diagnostics);

        Assert.Contains("band", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c=87.5", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalculateRenderSize_PreservesAspectRatioWithinViewport()
    {
        var (width, height) = AsciiConsoleRenderer.CalculateRenderSize(200, 100, 80, 24);

        Assert.True(width >= 40, $"Expected a wide preview, but got width {width}");
        Assert.True(height >= 12, $"Expected a tall enough preview, but got height {height}");
    }
}
