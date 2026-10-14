using System.Text.Json;

using SourdoughMonitor.Config;

namespace SourdoughMonitor.Vision;

public readonly record struct PointD(double X, double Y);

/// <summary>Axis-aligned bounds of the jar outline in full-frame pixels.</summary>
public readonly record struct JarBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;
}

/// <summary>What the LLM returned, still in the 0..1000 grid of the image it was shown.</summary>
public sealed record LlmAnnotation(
    bool JarVisible,
    IReadOnlyList<PointD> JarOutline,
    IReadOnlyList<PointD> DoughSurface,
    double Confidence);

/// <summary>Result of validating an annotation and mapping it to full-frame pixels.
/// <see cref="Outcome"/> is "detected", "no_jar", "no_surface" or "llm_rejected".</summary>
public sealed record LlmInterpretation(
    string Outcome,
    string? Reason,
    JarBox? Jar,
    IReadOnlyList<PointD> Outline,
    IReadOnlyList<PointD> Surface,
    double? SurfaceRow);

public static class LlmAnnotationInterpreter
{
    /// <summary>Prompt sent with every frame. Coordinates are a 0..1000 grid because models
    /// localise far better on a fixed normalised grid than in arbitrary pixel sizes.</summary>
    public const string SystemPrompt =
        "You are a precise computer-vision annotator. You locate objects in images and answer with JSON only.";

    public const string UserPrompt =
        """
        The image shows a glass jar containing sourdough starter or dough, taken by a fixed camera, possibly in dim light or lit from behind by an LED. The jar may be tilted, and its top may be cut off by the image edge.

        Coordinates are integers on a 0-1000 grid over THIS image: x=0 is the left edge, x=1000 the right edge, y=0 the top edge, y=1000 the bottom edge.

        Return:
        - jar_visible: false if no jar is visible.
        - jar_outline: the outer silhouette of the WHOLE jar as ordered points, clockwise, starting at the top of the left wall: down the left wall (at least 3 points), along the jar bottom (at least 3 points, including both bottom corners), up the right wall (at least 3 points). The outline must hug the glass of the jar itself: never include the wall behind it, the shelf, the lid or any lamp glow around it. If the jar top is cut off by the image edge, put the top points on that edge (y=0).
        - dough_surface: the top surface of the dough at the FRONT glass wall, the line where the dough meets the glass nearest to the camera, as 5 to 9 points ordered from the left wall to the right wall. Follow the curve if it is curved. Do NOT mark the far (back) edge of the dough surface, the back wall of the jar, reflections, or anything seen through the jar at a higher position than the front surface line.
        - confidence: your confidence from 0 to 1.
        If the jar is empty, return an empty dough_surface list.
        """;

    public static readonly object ResponseSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "jar_visible", "jar_outline", "dough_surface", "confidence" },
        properties = new
        {
            jar_visible = new { type = "boolean" },
            jar_outline = PointList(),
            dough_surface = PointList(),
            confidence = new { type = "number" }
        }
    };

    private static object PointList() => new
    {
        type = "array",
        items = new
        {
            type = "array",
            items = new { type = "number" },
            minItems = 2,
            maxItems = 2
        }
    };

    /// <summary>Parses the model's text, tolerating code fences and surrounding prose.</summary>
    public static LlmAnnotation? Parse(string content, out string? error)
    {
        error = null;
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "no_json";
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;
            var visible = root.TryGetProperty("jar_visible", out var v) && v.ValueKind == JsonValueKind.True;
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetDouble()
                : 0.0;
            var outline = ReadPoints(root, "jar_outline");
            var surface = ReadPoints(root, "dough_surface");
            if (outline is null || surface is null)
            {
                error = "bad_points";
                return null;
            }
            return new LlmAnnotation(visible, outline, surface, confidence);
        }
        catch (JsonException)
        {
            error = "bad_json";
            return null;
        }
    }

    private static List<PointD>? ReadPoints(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr))
            return [];
        if (arr.ValueKind != JsonValueKind.Array) return null;
        var points = new List<PointD>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2
                || item[0].ValueKind != JsonValueKind.Number || item[1].ValueKind != JsonValueKind.Number)
                return null;
            points.Add(new PointD(item[0].GetDouble(), item[1].GetDouble()));
        }
        return points;
    }

    /// <summary>Validates the annotation and maps it from the 0..1000 grid to full-frame pixels.
    /// <paramref name="crop"/> is the region of the full frame the model saw.</summary>
    public static LlmInterpretation Interpret(
        LlmAnnotation a, (int X, int Y, int Width, int Height) crop, LlmOptions options)
    {
        LlmInterpretation Reject(string reason) => new("llm_rejected", reason, null, [], [], null);

        if (!a.JarVisible)
            return new("no_jar", "jar_not_visible", null, [], [], null);
        if (a.Confidence < options.MinConfidence)
            return Reject("low_confidence");
        if (a.JarOutline.Count < 4)
            return Reject("outline_points");
        if (a.JarOutline.Concat(a.DoughSurface).Any(p => p.X < -50 || p.X > 1050 || p.Y < -50 || p.Y > 1050))
            return Reject("out_of_grid");

        PointD Map(PointD p) => new(
            crop.X + Math.Clamp(p.X, 0, 1000) / 1000.0 * crop.Width,
            crop.Y + Math.Clamp(p.Y, 0, 1000) / 1000.0 * crop.Height);
        var outline = a.JarOutline.Select(Map).ToList();
        var box = new JarBox(
            outline.Min(p => p.X), outline.Min(p => p.Y), outline.Max(p => p.X), outline.Max(p => p.Y));
        if (box.Width < 0.08 * crop.Width || box.Height < 0.15 * crop.Height || box.Height < 0.5 * box.Width)
            return new("llm_rejected", "jar_shape", box, outline, [], null);

        if (a.DoughSurface.Count == 0)
            return new("no_surface", "empty_surface", box, outline, [], null);
        if (a.DoughSurface.Count < 2)
            return new("llm_rejected", "surface_points", box, outline, [], null);
        var surface = a.DoughSurface.Select(Map).OrderBy(p => p.X).ToList();
        var slack = 0.05 * box.Width;
        if (surface.Any(p => p.X < box.Left - slack || p.X > box.Right + slack
                             || p.Y < box.Top - 0.05 * box.Height || p.Y > box.Bottom))
            return new("llm_rejected", "surface_outside_jar", box, outline, surface, null);
        if (surface[^1].X - surface[0].X < 0.4 * box.Width)
            return new("llm_rejected", "surface_span", box, outline, surface, null);

        var row = CentralLevel(surface, box);
        if (row >= box.Bottom - 0.03 * box.Height)
            return new("no_surface", "surface_at_bottom", box, outline, surface, row);
        return new("detected", null, box, outline, surface, row);
    }

    /// <summary>Surface row over the central 60% of the jar width: mean of the curve sampled
    /// at seven evenly spaced columns, so meniscus climb at the walls does not bias the level.</summary>
    public static double CentralLevel(IReadOnlyList<PointD> surface, JarBox box)
    {
        const int samples = 7;
        var x0 = box.Left + 0.2 * box.Width;
        var x1 = box.Right - 0.2 * box.Width;
        var sum = 0.0;
        for (var i = 0; i < samples; i++)
            sum += CurveY(surface, x0 + (x1 - x0) * i / (samples - 1));
        return sum / samples;
    }

    /// <summary>Linear interpolation of a left-to-right curve; clamped to its end points.</summary>
    public static double CurveY(IReadOnlyList<PointD> curve, double x)
    {
        if (x <= curve[0].X) return curve[0].Y;
        for (var i = 1; i < curve.Count; i++)
        {
            if (x > curve[i].X) continue;
            var dx = curve[i].X - curve[i - 1].X;
            if (dx <= 0) return curve[i].Y;
            return curve[i - 1].Y + (curve[i].Y - curve[i - 1].Y) * (x - curve[i - 1].X) / dx;
        }
        return curve[^1].Y;
    }
}
