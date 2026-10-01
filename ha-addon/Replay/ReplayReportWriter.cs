using System.Globalization;
using System.Net;
using System.Text;

namespace SourdoughMonitor.Replay;

/// <summary>Writes the replay report.html: outcome timeline with day/night shading,
/// rise curve as an inline SVG, anomaly table and annotated-frame thumbnails.
/// Self-contained (inline CSS/SVG), so the report works from any directory.</summary>
public static class ReplayReportWriter
{
    public static void WriteHtml(string path, ReplaySummary summary, IReadOnlyList<ReplayFrameRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Sourdough replay report</title>");
        sb.Append("<style>");
        sb.Append("body{font:14px system-ui,Segoe UI,sans-serif;margin:16px;background:#fafafa;color:#222}");
        sb.Append("h1{font-size:20px;margin:0 0 8px}h2{font-size:16px;margin:24px 0 6px}");
        sb.Append("table{border-collapse:collapse;background:#fff;font-size:12px}");
        sb.Append("th,td{border:1px solid #ddd;padding:3px 7px;text-align:left;white-space:nowrap}");
        sb.Append("th{background:#f0f0f0}");
        sb.Append("tr.detected td.outcome{color:#070}");
        sb.Append("tr.dark td.outcome{color:#88f;font-weight:bold}");
        sb.Append("tr.no-surface td.outcome,tr.failed td.outcome{color:#c33}");
        sb.Append("tr.rejected td.gate{color:#c33;font-weight:bold}");
        sb.Append("tr.rejected td.gate::after{content:\" ⚠\"}");
        sb.Append(".stat{display:inline-block;margin:2px 12px 2px 0;padding:4px 10px;background:#fff;border:1px solid #ddd;border-radius:4px}");
        sb.Append(".thumb{max-width:220px;max-height:220px;display:block;margin:2px}");
        sb.Append("</style></head><body>");
        sb.Append("<h1>Sourdough replay report</h1>");
        sb.Append($"<div>Input: <code>{WebUtility.HtmlEncode(summary.InputFolder)}</code> | Config: <code>{WebUtility.HtmlEncode(summary.ConfigPath ?? "defaults")}</code>");
        if (summary.RoiApplied is { } roi)
        {
            sb.Append($" | ROI: ({roi.X},{roi.Y}) {roi.W}x{roi.H}");
        }
        sb.Append("</div>");

        sb.Append("<h2>Frames</h2>");
        sb.Append(
            $"<span class=\"stat\">Total {summary.FrameCount}</span>"
            + $"<span class=\"stat\">Detected {summary.Detected}</span>"
            + $"<span class=\"stat\">Dark {summary.DarkFrames}</span>"
            + $"<span class=\"stat\">No surface {summary.NoSurface}</span>"
            + $"<span class=\"stat\">Decode failed {summary.DecodeFailed}</span>"
            + $"<span class=\"stat\">Readings unavailable {summary.ReadingsUnavailable}</span>"
            + $"<span class=\"stat\">Mtime fallback {summary.MtimeFallback}</span>");
        var methods = string.Join(", ", summary.MethodCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"));
        sb.Append($"<div>Methods: {WebUtility.HtmlEncode(methods)}</div>");
        if (summary.LastRiseReading is { } r)
        {
            sb.Append(
                $"<div>Final rise reading: {r.RisePercent:F1}% | "
                + $"rate {r.RiseRatePercentPerHour?.ToString("F1") ?? "-"}%/h | "
                + $"predicted peak {r.PredictedPeakPercent?.ToString("F0") ?? "-"}% at "
                + $"{r.PredictedPeakTime?.ToString("yyyy-MM-dd HH:mm") ?? "-"} | peaked {r.Peaked}</div>");
        }
        if (summary.Warnings.Count > 0)
        {
            sb.Append("<div style=\"color:#c33\">Warnings:<br>");
            foreach (var warning in summary.Warnings)
            {
                sb.Append($"{WebUtility.HtmlEncode(warning)}<br>");
            }
            sb.Append("</div>");
        }

        sb.Append("<h2>Rise curve</h2>");
        sb.Append(BuildRiseCurveSvg(rows));

        sb.Append("<h2>Outcome timeline</h2>");
        sb.Append(BuildOutcomeTimeline(summary, rows));

        sb.Append("<h2>Anomalies &amp; rejections</h2>");
        sb.Append(BuildAnomalyTable(rows));

        sb.Append("<h2>Annotated frames</h2>");
        sb.Append(BuildThumbnails(rows));

        sb.Append("</body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Rise percent (detected rows) as inline SVG with one labelled point per day.</summary>
    private static string BuildRiseCurveSvg(IReadOnlyList<ReplayFrameRow> rows)
    {
        var points = rows
            .Where(r => r.RisePercent is not null && r.NewSession is false)
            .Select(r => (r.Time, Rise: r.RisePercent!.Value))
            .ToList();
        if (points.Count < 2) return "<p>No continuous rise series (too few detected frames).</p>";
        const int width = 1100, height = 320, left = 60, top = 20;
        var plotWidth = width - left - 20;
        var plotHeight = height - top - 50;
        var t0 = points[0].Time;
        var t1 = points[^1].Time;
        var span = (t1 - t0).TotalHours;
        if (span <= 0) span = 1;
        var yMax = Math.Max(20, points.Max(p => p.Rise) * 1.1);
        var dayStart = t0.Date.AddDays(1);
        var dayLines = new StringBuilder();
        while (dayStart <= t1)
        {
            var x = left + (dayStart - t0).TotalHours / span * plotWidth;
            dayLines.Append(
                $"<line x1=\"{x:F1}\" y1=\"{top}\" x2=\"{x:F1}\" y2=\"{top + plotHeight}\" stroke=\"#ddd\" stroke-dasharray=\"3,3\"/>");
            dayLines.Append(
                $"<text x=\"{x:F1}\" y=\"{top + plotHeight + 14}\" font-size=\"10\" fill=\"#888\" text-anchor=\"middle\">{dayStart:MM-dd}</text>");
            dayStart = dayStart.AddDays(1);
        }
        var curve = string.Join(" ", points.Select(p =>
        {
            var x = left + (p.Time - t0).TotalHours / span * plotWidth;
            var y = top + (1 - p.Rise / yMax) * plotHeight;
            return $"{x:F1},{y:F1}";
        }));
        var svg = new StringBuilder();
        svg.Append(
            $"<svg width=\"{width}\" height=\"{height}\" style=\"background:#fff;border:1px solid #ddd\">");
        svg.Append(dayLines.ToString());
        svg.Append(
            $"<polyline points=\"{curve}\" fill=\"none\" stroke=\"#c33\" stroke-width=\"1.5\"/>");
        svg.Append(
            $"<line x1=\"{left}\" y1=\"{top + plotHeight}\" x2=\"{left + plotWidth}\" y2=\"{top + plotHeight}\" stroke=\"#999\"/>");
        svg.Append(
            $"<line x1=\"{left}\" y1=\"{top}\" x2=\"{left}\" y2=\"{top + plotHeight}\" stroke=\"#999\"/>");
        for (var pct = 0; pct <= yMax; pct += yMax > 200 ? 50 : 10)
        {
            var y = top + (1 - pct / yMax) * plotHeight;
            svg.Append(
                $"<text x=\"{left - 6}\" y=\"{y + 3:F1}\" font-size=\"10\" fill=\"#888\" text-anchor=\"end\">{pct}%</text>");
        }
        svg.Append($"<text x=\"{left + 6}\" y=\"{top - 6}\" font-size=\"10\" fill=\"#888\">{t0:yyyy-MM-dd HH:mm} → {t1:yyyy-MM-dd HH:mm} ({span:F0}h)</text>");
        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>Horizontal bar per hour with outcome colors; darker cells are night frames.</summary>
    private static string BuildOutcomeTimeline(ReplaySummary summary, IReadOnlyList<ReplayFrameRow> rows)
    {
        if (rows.Count == 0) return "";
        var palette = new Dictionary<string, string>
        {
            ["detected"] = "#2b2",
            ["dark_frame"] = "#aaf",
            ["no_surface"] = "#f66",
            ["decode_failed"] = "#000",
            ["no_jar"] = "#f99"
        };
        var sb = new StringBuilder();
        var t0 = rows[0].Time;
        var span = (rows[^1].Time - t0).TotalHours + 1;
        const int left = 60;
        var width = Math.Max(800, Math.Min(1400, (int)(span * 4)));
        var plotWidth = width - left - 20;
        sb.Append(
            $"<svg width=\"{width}\" height=\"70\" style=\"background:#fff;border:1px solid #ddd\">");
        foreach (var r in rows)
        {
            var color = palette.GetValueOrDefault(r.Outcome, "#ccc");
            var isNight = r.FrameP90 is { } p90 && p90 < 25;
            var x = left + (r.Time - t0).TotalHours / Math.Max(1, span) * plotWidth;
            var w = Math.Max(2, plotWidth / Math.Max(1, span) * 0.8);
            sb.Append($"<rect x=\"{x:F1}\" y=\"{(isNight ? 30 : 10)}\" width=\"{w:F1}\" height=\"15\" fill=\"{color}\"/>");
        }
        var hourStart = t0.Date.AddHours(t0.Hour + 1);
        while (hourStart <= rows[^1].Time)
        {
            var x = left + (hourStart - t0).TotalHours / Math.Max(1, span) * plotWidth;
            sb.Append(
                $"<text x=\"{x:F1}\" y=\"58\" font-size=\"9\" fill=\"#888\" text-anchor=\"middle\">{hourStart:MM-dd HH}</text>");
            hourStart = hourStart.AddHours(6);
        }
        sb.Append("</svg>");
        sb.Append(
            "<div style=\"font-size:11px;color:#555\">green=detected · light blue=dark frame (rejected by gate) · "
            + "red=no surface · black=decode failed · lower row = dark frames (P90 &lt; 25)</div>");
        return sb.ToString();
    }

    private static string BuildAnomalyTable(IReadOnlyList<ReplayFrameRow> rows)
    {
        var anomalies = rows
            .Where(r => r.Outcome != "detected" || r.Reading == "unavailable" || r.NewSession)
            .ToList();
        if (anomalies.Count == 0) return "<p>None — every frame detected cleanly.</p>";
        var sb = new StringBuilder();
        sb.Append("<table><tr><th>Time</th><th>Outcome</th><th>Method</th><th>P90</th><th>Contrast</th>"
            + "<th>BandTop</th><th>Final</th><th>Dough h</th><th>Reading</th><th>NewSession</th><th>Debug</th></tr>");
        foreach (var r in anomalies)
        {
            var css = r.Outcome == "dark_frame" ? "dark"
                : r.Outcome == "detected" ? "detected"
                : r.Outcome is "no_surface" or "decode_failed" or "no_jar" ? r.Outcome == "decode_failed" ? "failed" : "no-surface"
                : r.Reading == "unavailable" ? "rejected"
                : "";
            sb.Append(
                $"<tr class=\"{css}\"><td>{r.Time:yyyy-MM-dd HH:mm:ss}</td>"
                + $"<td class=\"outcome\">{WebUtility.HtmlEncode(r.Outcome)}</td>"
                + $"<td>{WebUtility.HtmlEncode(r.Method ?? "")}</td>"
                + $"<td>{F1(r.FrameP90)}</td><td>{F1(r.BandContrast)}</td>"
                + $"<td>{r.BandTopRow?.ToString() ?? ""}</td><td>{r.FinalRow?.ToString() ?? ""}</td>"
                + $"<td>{F1(r.DoughHeightPx)}</td>"
                + $"<td class=\"gate\">{WebUtility.HtmlEncode(r.Reading)}</td>"
                + $"<td>{(r.NewSession ? "yes" : "")}</td>"
                + $"<td>{(r.DebugImage is null ? "" : "img")}</td></tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    /// <summary>All annotated debug images with their frame time; sized small, one per frame.</summary>
    private static string BuildThumbnails(IReadOnlyList<ReplayFrameRow> rows)
    {
        var withImages = rows.Where(r => r.DebugImage is not null).ToList();
        if (withImages.Count == 0) return "<p>No annotated frames written.</p>";
        var sb = new StringBuilder();
        foreach (var r in withImages)
        {
            var outcome = WebUtility.HtmlEncode(r.Outcome);
            var img = WebUtility.HtmlEncode(r.DebugImage!);
            sb.Append(
                $"<div style=\"display:inline-block;margin:4px;vertical-align:top\">"
                + $"<img class=\"thumb\" src=\"{img}\" loading=\"lazy\">"
                + $"<div style=\"font-size:10px\">{r.Time:MM-dd HH:mm} · {outcome}</div></div>");
        }
        return sb.ToString();
    }

    private static string F1(double? value) =>
        value?.ToString("F1", CultureInfo.InvariantCulture) ?? "";
}
