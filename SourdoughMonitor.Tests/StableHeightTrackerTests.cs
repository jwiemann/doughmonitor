using SourdoughMonitor.Analysis;
using SourdoughMonitor.Vision;

namespace SourdoughMonitor.Tests;

public class StableHeightTrackerTests
{
    [Fact]
    public void AgreeingWalls_AddTheDomeOffsetToTheDomeTop()
    {
        var tracker = new StableHeightTracker();
        StableHeight last = null!;
        for (var i = 0; i < 10; i++) last = tracker.Update(new WallContacts(400, 418, 422));

        Assert.Equal(HeightSource.WallAndDome, last.Source);
        Assert.Equal(420, last.HeightRow);
        Assert.Equal(20, last.DomeOffsetPx);
    }

    [Fact]
    public void UnreliableWalls_FallBackToTheDomeTopAlone()
    {
        var tracker = new StableHeightTracker();
        StableHeight last = null!;
        for (var i = 0; i < 10; i++) last = tracker.Update(new WallContacts(400, 430, 470));

        Assert.Equal(HeightSource.ApexOnly, last.Source);
        Assert.Equal(400, last.HeightRow);
    }

    [Fact]
    public void RaggedSurface_UsesTheClassicLineInsteadOfTheTopmostSpike()
    {
        var tracker = new StableHeightTracker();
        StableHeight last = null!;
        for (var i = 0; i < 10; i++) last = tracker.Update(new WallContacts(420, null, null, 440), 437);

        Assert.Equal(DomeSource.Classic, last.DomeSource);
        Assert.Equal(437, last.HeightRow);
    }

    [Fact]
    public void SmoothDome_KeepsTheMeasuredTop()
    {
        var tracker = new StableHeightTracker();
        StableHeight last = null!;
        for (var i = 0; i < 10; i++) last = tracker.Update(new WallContacts(400, null, null, 409), 405);

        Assert.Equal(DomeSource.Top, last.DomeSource);
        Assert.Equal(400, last.HeightRow);
    }
}
