using SourdoughMonitor.Replay;

namespace SourdoughMonitor.Tests;

public class SnapshotTimestampResolverTests
{
    [Theory]
    [InlineData("20260928_120000123.jpg", 2026, 9, 28, 12, 0, 0, 123)]
    [InlineData("20260928_120000.jpg", 2026, 9, 28, 12, 0, 0, 0)]
    [InlineData("IMG_2026-09-28_12-00-00.jpg", 2026, 9, 28, 12, 0, 0, 0)]
    [InlineData("IMG20260928_120000.jpg", 2026, 9, 28, 12, 0, 0, 0)]
    [InlineData("2026-09-28 12.00.00.jpg", 2026, 9, 28, 12, 0, 0, 0)]
    [InlineData("20260928T120000.jpg", 2026, 9, 28, 12, 0, 0, 0)]
    [InlineData("2026-09-28T12:00:00.5.jpg", 2026, 9, 28, 12, 0, 0, 500)]
    [InlineData("img-20260928-120000.jpg", 2026, 9, 28, 12, 0, 0, 0)]
    [InlineData("2026_09_28_12_00_00.jpg", 2026, 9, 28, 12, 0, 0, 0)]
    public void TryParse_ReadsTimestampFromFileName(
        string fileName,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        int milliseconds)
    {
        var parsed = SnapshotTimestampResolver.TryParse(fileName, out var timestamp);
        Assert.True(parsed, fileName);
        Assert.Equal(year, timestamp.Year);
        Assert.Equal(month, timestamp.Month);
        Assert.Equal(day, timestamp.Day);
        Assert.Equal(hour, timestamp.Hour);
        Assert.Equal(minute, timestamp.Minute);
        Assert.Equal(second, timestamp.Second);
        Assert.Equal(milliseconds, timestamp.Millisecond);
    }

    [Fact]
    public void TryParse_ParsesArchiveNamesAsUtc()
    {
        // The addon archives under UtcNow: parsing the digits as local time shifted every
        // replay timestamp (and its report axis) by the machine's UTC offset.
        var parsed = SnapshotTimestampResolver.TryParse("20261006_173047580.jpg", out var timestamp);
        Assert.True(parsed);
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 17, 30, 47, 580, TimeSpan.Zero), timestamp);
    }

    [Theory]
    [InlineData("frame_0042.jpg")]
    [InlineData("snapshot.jpg")]
    [InlineData("")]
    public void TryParse_RejectsNonTimestampNames(string fileName)
    {
        Assert.False(SnapshotTimestampResolver.TryParse(fileName, out _));
    }

    [Fact]
    public void TryParse_RejectsDigitRunsTooLongForDate()
    {
        // Eleven digits like a phone number would be misread as year+month without
        // the digit-run lookarounds.
        Assert.False(SnapshotTimestampResolver.TryParse("20260928120.jpg", out _));
    }
}
