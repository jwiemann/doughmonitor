using System.Globalization;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SourdoughMonitor.Analysis;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Services;

public sealed class AsciiConsoleRenderer
{
    private static readonly string[] Palette = [" ", ".", ":", "-", "=", "+", "*", "#", "%", "@"];
    private byte[]? _lastFrame;
    private string _lastStatus = "starting";
    private LevelMeasurement? _lastMeasurement;
    private JarLevelDetector.DetectionDiagnostics? _lastDiagnostics;
    private GrowthAnalysis? _lastGrowthAnalysis;
    private bool _debugOverlayEnabled = true;

    public static (int Width, int Height) CalculateRenderSize(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (Math.Max(1, maxWidth), Math.Max(1, maxHeight));
        }

        const double TerminalAspectRatio = 2.0;
        var sourceAspectRatio = (double)sourceWidth / sourceHeight;
        var targetWidth = Math.Max(40, maxWidth);
        var targetHeight = (int)Math.Round(targetWidth / (sourceAspectRatio * TerminalAspectRatio));

        if (targetHeight > maxHeight)
        {
            targetHeight = maxHeight;
            targetWidth = (int)Math.Round(targetHeight * sourceAspectRatio * TerminalAspectRatio);
        }

        if (targetWidth < 40)
        {
            targetWidth = 40;
        }

        return (Math.Max(1, targetWidth), Math.Max(1, targetHeight));
    }

    public string RenderAscii(byte[]? imageData, int width = 80, int height = 40)
    {
        if (imageData is null || imageData.Length == 0)
        {
            return string.Join(Environment.NewLine, Enumerable.Repeat("[no frame]", Math.Max(1, height)));
        }

        try
        {
            using var image = Image.Load<Rgba32>(imageData);
            var (targetWidth, targetHeight) = CalculateRenderSize(image.Width, image.Height, width, height);
            image.Mutate(ctx => ctx.Resize(targetWidth, targetHeight));

            var sb = new StringBuilder();
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var pixel = image[x, y];
                    var luminance = (0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B) / 255.0;
                    var index = (int)(luminance * (Palette.Length - 1));
                    index = Math.Clamp(index, 0, Palette.Length - 1);
                    sb.Append(Palette[index]);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch
        {
            return string.Join(Environment.NewLine, Enumerable.Repeat("[invalid frame]", Math.Max(1, height)));
        }
    }

    public void SetMeasurement(LevelMeasurement? measurement) => _lastMeasurement = measurement;

    public void SetDetectionDiagnostics(JarLevelDetector.DetectionDiagnostics? diagnostics) => _lastDiagnostics = diagnostics;

    public void SetGrowthAnalysis(GrowthAnalysis? analysis) => _lastGrowthAnalysis = analysis;

    public string BuildDisplayText(byte[] imageData, int width = 80, int height = 40, string? status = null, LevelMeasurement? measurement = null, JarLevelDetector.DetectionDiagnostics? diagnostics = null)
    {
        var activeDiagnostics = diagnostics ?? _lastDiagnostics;
        var art = RenderAscii(imageData, width, height);
        var artLines = art.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (_debugOverlayEnabled && measurement is not null)
        {
            artLines = ApplyDebugOverlay(artLines, measurement, imageData, width, height);
        }

        var artWidth = artLines.Length > 0 ? artLines.Max(GetVisibleTextLength) : width;
        var boxWidth = Math.Max(24, artWidth + 2);

        var statusText = status ?? (imageData.Length > 0 ? "frame ok" : "no frame");
        var timeText = DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var jarRange = measurement is null
            ? "n/a"
            : $"{measurement.JarTopPx:0.0}..{measurement.JarBottomPx:0.0}";
        var doughTop = measurement is null
            ? "n/a"
            : $"{measurement.DoughTopPx:0.0}";
        var doughHeight = measurement is null
            ? "n/a"
            : $"{measurement.DoughHeightPx:0.0}";

        var titleText = $"preview {width}x{height} · {timeText} · {statusText}";
        var growthText = _lastGrowthAnalysis is null
            ? "growth collecting"
            : $"growth {FormatGrowthAnalysis(_lastGrowthAnalysis)}";
        var diagnosticText = activeDiagnostics is null
            ? string.Empty
            : $" • {activeDiagnostics.Method}, c={activeDiagnostics.BandContrast.ToString("0.0", CultureInfo.InvariantCulture)}";
        var measurementText = $"jar {jarRange} • dough {doughTop}{diagnosticText} • h {doughHeight}px";
        var combinedMeasurementText = $"{measurementText} • {growthText}";
        var titleLine = $"│ {titleText}{new string(' ', Math.Max(0, boxWidth - titleText.Length - 2))} │";
        var measurementLine = $"│ {combinedMeasurementText}{new string(' ', Math.Max(0, boxWidth - combinedMeasurementText.Length - 2))} │";
        var legendLine = $"│ {FormatLegendText()} {new string(' ', Math.Max(0, boxWidth - FormatLegendText().Length - 2))} │";

        const string reset = "\u001b[0m";
        const string headerColor = "\u001b[36m";
        var headerLines = new List<string>
        {
            $"{headerColor}┌{new string('─', boxWidth)}┐{reset}",
            $"{headerColor}{titleLine}{reset}",
            $"{headerColor}{measurementLine}{reset}",
            $"{headerColor}{legendLine}{reset}",
            $"{headerColor}└{new string('─', boxWidth)}┘{reset}"
        };

        var lines = new List<string>();
        lines.AddRange(headerLines);
        lines.AddRange(ColorizeArtLines(artLines));
        if (_debugOverlayEnabled && measurement is not null)
        {
            lines.Add("overlay: jar bottom = cyan, dough = red");
        }
        return string.Join(Environment.NewLine, lines.Take(40));
    }

    private static string FormatLegendText()
    {
        const string reset = "\u001b[0m";
        const string jarColor = "\u001b[96m";
        const string doughColor = "\u001b[91m";
        return $"{jarColor}══ jar{reset} • {doughColor}██ dough{reset} • debug overlay";
    }

    private static string FormatGrowthAnalysis(GrowthAnalysis analysis)
    {
        var phase = analysis.Phase.ToString().ToLowerInvariant();
        var trendArrow = analysis.Trend switch
        {
            RiseTrend.Accelerating => "↑",
            RiseTrend.Decelerating => "↓",
            RiseTrend.Steady => "→",
            _ => "•"
        };

        var targetHint = analysis.EstimatedTargetTime is not null
            ? $" • target ~{analysis.EstimatedTargetTime.Value:HH:mm}" 
            : string.Empty;
        var peakHint = analysis.PredictedPeakTime is not null
            ? $" • peak ~{analysis.PredictedPeakTime.Value:HH:mm}" 
            : string.Empty;

        return $"{analysis.CurrentHeightPx:0.0}px • {analysis.GrowthFactor:0.00}x • {analysis.RiseRatePxPerHour:0.0}px/h {trendArrow} •{targetHint}{peakHint} {phase}";
    }

    private static IReadOnlyList<string> ColorizeArtLines(IReadOnlyList<string> artLines) => artLines;

    public void ToggleDebugOverlay()
    {
        _debugOverlayEnabled = !_debugOverlayEnabled;
    }

    private static string[] ApplyDebugOverlay(IReadOnlyList<string> artLines, LevelMeasurement measurement, byte[] imageData, int width, int height)
    {
        try
        {
            using var image = Image.Load<Rgba32>(imageData);
            var (targetWidth, targetHeight) = CalculateRenderSize(image.Width, image.Height, width, height);

            var overlayLines = artLines.ToArray();
            var contentRows = Math.Max(1, overlayLines.Length);
            var jarBottom = MapMeasurementToPreviewRow(measurement.JarBottomPx, image.Height, contentRows);
            var doughRow = MapMeasurementToPreviewRow(measurement.DoughTopPx, image.Height, contentRows);

            const string reset = "\u001b[0m";
            const string jarColor = "\u001b[96m";
            const string doughColor = "\u001b[91m";

            if (jarBottom >= 0 && jarBottom < overlayLines.Length)
            {
                overlayLines[jarBottom] = ColorizeLine(overlayLines[jarBottom], jarColor, reset);
            }

            if (doughRow >= 0 && doughRow < overlayLines.Length)
            {
                overlayLines[doughRow] = ColorizeLine(overlayLines[doughRow], doughColor, reset);
            }

            return overlayLines;
        }
        catch
        {
            return artLines.ToArray();
        }
    }

    private static string ColorizeLine(string line, string color, string reset)
    {
        var sb = new StringBuilder(line.Length + 16);
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '\u001b' && i + 1 < line.Length && line[i + 1] == '[')
            {
                var j = i + 2;
                while (j < line.Length && line[j] != 'm') j++;
                sb.Append(line, i, j - i + 1);
                i = j + 1;
                continue;
            }
            sb.Append(color);
            sb.Append(line[i]);
            sb.Append(reset);
            i++;
        }
        return sb.ToString();
    }

    private static string OverlayLine(string line, int startX, int endX, char marker, string colorCode)
    {
        var sb = new StringBuilder(line.Length + 16);
        for (var x = 0; x < line.Length; x++)
        {
            if (x >= startX && x < endX)
            {
                sb.Append(colorCode);
                sb.Append(marker);
                sb.Append("\u001b[0m");
            }
            else
            {
                sb.Append(line[x]);
            }
        }

        return sb.ToString();
    }

    private static int GetVisibleTextLength(string line)
    {
        var length = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\u001b' && i + 1 < line.Length && line[i + 1] == '[')
            {
                var j = i + 2;
                while (j < line.Length && line[j] != 'm')
                {
                    j++;
                }

                i = j;
                continue;
            }

            length++;
        }

        return length;
    }

    private static int MapMeasurementToPreviewRow(double measurementPx, int sourceHeight, int contentRows)
    {
        if (sourceHeight <= 0 || contentRows <= 0)
        {
            return 0;
        }

        var row = (int)Math.Round(measurementPx / sourceHeight * contentRows);
        return Math.Clamp(row, 0, contentRows - 1);
    }

    private static int ClampToRange(int value, int min, int max)
    {
        return Math.Clamp(value, min, max);
    }

    public void RenderLoop(Func<byte[]> getFrame, TimeSpan interval, CancellationToken ct)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;

        while (!ct.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.D)
                {
                    ToggleDebugOverlay();
                }
            }
            byte[] frame;
            try
            {
                var frameTask = Task.Run(() =>
                {
                    try
                    {
                        return getFrame();
                    }
                    catch
                    {
                        return Array.Empty<byte>();
                    }
                }, ct);

                var completedTask = Task.WhenAny(frameTask, Task.Delay(TimeSpan.FromSeconds(2), ct)).GetAwaiter().GetResult();
                frame = completedTask == frameTask
                    ? frameTask.GetAwaiter().GetResult()
                    : Array.Empty<byte>();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                frame = Array.Empty<byte>();
            }

            if (frame.Length > 0)
            {
                _lastFrame = frame;
                _lastStatus = "frame ok";
            }
            else if (_lastFrame is not null)
            {
                frame = _lastFrame;
                _lastStatus = "last frame";
            }
            else
            {
                _lastStatus = "waiting for frame";
            }

            var display = BuildDisplayText(frame, 120, 36, _lastStatus, _lastMeasurement);
            Console.Clear();
            Console.WriteLine("Sourdough Monitor");
            Console.WriteLine(new string('-', 60));
            Console.WriteLine(display);
            Console.WriteLine();
            Console.WriteLine(_debugOverlayEnabled ? "Debug overlay on • press d to hide" : "Press d for debug overlay");
            Console.WriteLine("Live preview • press Ctrl+C to stop");
            Thread.Sleep(interval);
        }
    }
}
