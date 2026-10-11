using SourdoughMonitor.Config;

namespace SourdoughMonitor.Analysis;

/// <summary>Tracks consecutive <c>no_surface</c> detector outcomes after a confirmed peak:
/// a peaked session whose surface stays lost for
/// <see cref="AnalysisOptions.SurfaceLossAfterPeakMinutes"/> minutes is flagged in the
/// diagnostics (the detector may have lost a collapsed surface). Any other outcome — a
/// measurement, a dark night frame, a missing jar — resets the streak: only an unbroken
/// run of surface misses counts as a loss.</summary>
public sealed class SurfaceLossMonitor(AnalysisOptions options)
{
    private DateTimeOffset? _since;

    /// <summary>Observes one sampling cycle's detector outcome. Returns the length of the
    /// current unbroken <c>no_surface</c> streak in minutes once the session is peaked and
    /// the streak has reached <see cref="AnalysisOptions.SurfaceLossAfterPeakMinutes"/>;
    /// null otherwise.</summary>
    public double? Observe(string outcome, bool peaked, DateTimeOffset now)
    {
        if (outcome != "no_surface")
        {
            _since = null;
            return null;
        }
        _since ??= now;
        var minutes = (now - _since.Value).TotalMinutes;
        return peaked && minutes >= options.SurfaceLossAfterPeakMinutes ? minutes : null;
    }
}
