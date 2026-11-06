using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Analysis;

/// <summary>Where a <see cref="StableHeight"/> came from.</summary>
public enum DomeSource
{
    /// <summary>The measured topmost dough row (smooth dome).</summary>
    Top,

    /// <summary>The classic detector's surface line: on a ragged lump the topmost point is a single spike
    /// and hides slow growth, the classic line follows the bulk surface.</summary>
    Classic,
}

public enum HeightSource
{
    /// <summary>No dough top could be measured.</summary>
    None,

    /// <summary>Dome top only: the dough does not reliably touch both walls.</summary>
    ApexOnly,

    /// <summary>Dome top plus the smoothed dome offset measured from agreeing wall contacts.</summary>
    WallAndDome,
}

/// <summary>Per-frame height outcome. <paramref name="WallLevelRow"/> is the mean of both wall contacts
/// when they agree (else the side closest to the expected level). <paramref name="HeightRow"/> is the
/// steady line: dome top + trailing-median dome, or the dome top alone when walls are unreliable.</summary>
public sealed record StableHeight(
    double? DomeTopRow, double? WallLevelRow, double? DomeOffsetPx, double? HeightRow, HeightSource Source,
    DomeSource DomeSource = DomeSource.Top);

/// <summary>Combines <see cref="WallContacts"/> frame by frame (causal). The dome top is steady; the wall
/// contact is noisy and undefined when the dough does not touch the glass, so only the dome offset
/// (wall level - dome top) from frames where both walls agree is smoothed and added to the dome top.</summary>
public sealed class StableHeightTracker(
    int window = 41, int minReliable = 5, double maxSideDiffPx = 15, double raggedPx = 14, int raggedWindow = 15)
{
    private readonly List<double> _ragged = [];
    private readonly List<double> _levels = [];
    private int _levelRejects;
    private readonly List<double?> _offsets = [];
    private readonly List<double> _recent = [];

    /// <summary>Feeds one frame. <paramref name="classicTopRow"/> is the classic detector's dough-top row
    /// (frame coordinates); it replaces the dome top while the surface is ragged.</summary>
    public StableHeight Update(WallContacts contacts, double? classicTopRow = null)
    {
        if (contacts is { DomeTopRow: { } top, SurfaceMedianRow: { } med })
        {
            _ragged.Add(med - top);
            if (_ragged.Count > raggedWindow) _ragged.RemoveAt(0);
        }
        var useClassic = classicTopRow is not null && _ragged.Count > 0 && Median(_ragged) > raggedPx;
        var apex = useClassic ? classicTopRow : contacts.DomeTopRow;
        double? level = null;
        var reliable = false;
        if (contacts.LeftRow is { } l && contacts.RightRow is { } r && Math.Abs(l - r) <= maxSideDiffPx)
        {
            level = (l + r) / 2;
            reliable = true;
        }
        else if (apex is { } a && _recent.Count > 0 && (contacts.LeftRow ?? contacts.RightRow) is not null)
        {
            var expect = a + Median(_recent);
            var best = new[] { contacts.LeftRow, contacts.RightRow }
                .Where(v => v is not null).Select(v => v!.Value).OrderBy(v => Math.Abs(v - expect)).First();
            if (Math.Abs(best - expect) <= 20) level = best;
        }

        var shown = RejectOutlier(level);
        double? offset = reliable && apex is { } ap && level is { } lv ? lv - ap : null;
        if (offset is { } o)
        {
            _recent.Add(o);
            if (_recent.Count > 15) _recent.RemoveAt(0);
        }
        _offsets.Add(offset);
        if (_offsets.Count > window) _offsets.RemoveAt(0);

        var trusted = _offsets.Where(v => v is not null).Select(v => v!.Value).ToArray();
        // Enough agreeing frames, and they must be the majority of the window: a lump that only now and
        // then touches both walls stays on the dome top alone.
        double? smoothed = trusted.Length >= minReliable && trusted.Length * 5 >= _offsets.Count * 2 ? Median(trusted) : null;
        // Warm-up: until minReliable frames have been seen the wall-or-apex decision is open, and a
        // height would jump by the dome offset when it is made.
        if (_offsets.Count < minReliable) return new StableHeight(apex, shown, null, null, HeightSource.None, useClassic ? DomeSource.Classic : DomeSource.Top);
        var kind = useClassic ? DomeSource.Classic : DomeSource.Top;
        if (apex is null) return new StableHeight(null, shown, smoothed, null, HeightSource.None, kind);
        return smoothed is { } s
            ? new StableHeight(apex, shown, s, apex + s, HeightSource.WallAndDome, kind)
            : new StableHeight(apex, shown, null, apex, HeightSource.ApexOnly, kind);
    }

    /// <summary>Wall level against the trailing median of accepted levels: a jump of more than
    /// <see cref="LevelJumpPx"/> is a side flip or glare and is dropped (null), unless it persists for
    /// <see cref="LevelJumpFrames"/> frames, which makes it a real step (feeding, new dough).</summary>
    private double? RejectOutlier(double? level)
    {
        if (level is not { } lv) return null;
        if (_levels.Count >= 3 && Math.Abs(lv - Median(_levels)) > LevelJumpPx && ++_levelRejects < LevelJumpFrames)
            return null;
        _levelRejects = 0;
        _levels.Add(lv);
        if (_levels.Count > 9) _levels.RemoveAt(0);
        return lv;
    }

    private const double LevelJumpPx = 6;
    private const int LevelJumpFrames = 5;

    public void Reset()
    {
        _offsets.Clear();
        _recent.Clear();
        _ragged.Clear();
        _levels.Clear();
        _levelRejects = 0;
    }

    private static double Median(IReadOnlyCollection<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }
}
