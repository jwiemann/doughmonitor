using OpenCvSharp;

using SourdoughMonitor.Config;

namespace SourdoughMonitor.Vision;

/// <summary>Gray-level statistics and the dark-frame gate shared by every detector.</summary>
public static class FrameIntensity
{
    /// <summary>Median, 10th and 90th percentile gray level of an 8-bit single-channel image.</summary>
    public static (double Median, double P10, double P90) Percentiles(Mat gray)
    {
        using var hist = new Mat();
        Cv2.CalcHist([gray], [0], null, hist, 1, [256], [new Rangef(0, 256)]);
        var total = gray.Rows * gray.Cols;
        double Percentile(double fraction)
        {
            var target = total * fraction;
            var running = 0.0;
            for (var i = 0; i < 256; i++)
            {
                running += hist.At<float>(i);
                if (running >= target) return i;
            }
            return 255;
        }
        var median = 255.0;
        var half = total / 2.0;
        var cumulative = 0.0;
        for (var i = 0; i < 256; i++)
        {
            cumulative += hist.At<float>(i);
            if (cumulative >= half)
            {
                median = i;
                break;
            }
        }
        return (median, Percentile(0.10), Percentile(0.90));
    }

    /// <summary>True when nothing in the frame is bright enough to recognise a jar: the 90th
    /// percentile gray level is below <see cref="VisionOptions.MinFrameIntensity"/>. A backlit jar
    /// in a dark room passes because its lit part lifts P90. Contrast is deliberately not gated:
    /// a flat, bright daylight frame (pale jar on a pale wall) is still readable by a vision model.</summary>
    public static bool IsTooDark(double p90, VisionOptions options) =>
        p90 < options.MinFrameIntensity;
}
