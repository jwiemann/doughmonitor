using System.Text.RegularExpressions;

namespace SourdoughMonitor.Replay;

/// <summary>Resolves capture time from snapshot file names produced by the addon's snapshot
/// archive (yyyyMMdd_HHmmssfff), Frigate/HA exports, or typical camera naming
/// (IMG_2026-09-28_12-00-00). Falls back to file mtime in the caller; this resolver stays
/// pure so replay ordering is testable. Names are parsed as UTC: the addon archives under
/// DateTimeOffset.UtcNow, so a local-kind parse would shift every replay time by the
/// machine's UTC offset and make reports disagree with the wall clock.</summary>
public static partial class SnapshotTimestampResolver
{
    // All separators are optional: "20260928_120000123", "2026-09-28 12.00.00",
    // "20260928T120000", "2026-09-28T12:00:00.123" all parse. Digit-run lookaround
    // anchors reject longer digit runs instead of misreading them. Separator classes
    // keep the dash first/last so it stays a literal, not a character range.
    [GeneratedRegex(
        @"(?<!\d)(?<y>\d{4})[-_.]?(?<mo>\d{2})[-_.]?(?<d>\d{2})[T _-]?(?<h>\d{2})[-:_.]?(?<mi>\d{2})(?:[-:_.]?(?<s>\d{2}))?(?:[-_.]?(?<f>\d{1,3}))?(?!\d)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex TimestampPattern();

    public static bool TryParse(string fileName, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var match = TimestampPattern().Match(Path.GetFileName(fileName));
        if (!match.Success) return false;
        try
        {
            var utc = new DateTime(
                int.Parse(match.Groups["y"].Value),
                int.Parse(match.Groups["mo"].Value),
                int.Parse(match.Groups["d"].Value),
                int.Parse(match.Groups["h"].Value),
                int.Parse(match.Groups["mi"].Value),
                match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value) : 0,
                DateTimeKind.Utc);
            if (match.Groups["f"].Success)
            {
                // "5" means 0.5s: pad to milliseconds. "01" -> 010ms.
                utc = utc.AddMilliseconds(int.Parse(match.Groups["f"].Value.PadRight(3, '0')));
            }
            timestamp = new DateTimeOffset(utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Structurally valid digits that are not a real date/time.
            return false;
        }
    }
}
