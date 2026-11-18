using System.Drawing;
using Xunit;

namespace Sessionwright.Tests;

public class MonitorSpanTests
{
    // Three monitors side by side, as Windows lists them: the main one in the middle first.
    static readonly Rectangle Middle = new(0, 0, 1920, 1080), Left = new(-1920, 0, 1920, 1080), Right = new(1920, 0, 1920, 1080);
    static readonly Rectangle[] Row = [Middle, Left, Right];

    [Fact]
    public void MonitorsNextToEachOtherAreUsedAsChosen()
    {
        Assert.Equal([0, 1], MonitorSpan.Contiguous(Row, [1, 0]));
        Assert.Equal([0, 2], MonitorSpan.Contiguous(Row, [0, 2]));
        Assert.Equal([2], MonitorSpan.Contiguous(Row, [2]));
    }

    // Remote Desktop takes only contiguous monitors: the left and the right one alone gave a stretched picture.
    [Fact]
    public void TheMonitorBetweenTheChosenOnesComesAlong()
    {
        Assert.Equal([0, 1, 2], MonitorSpan.Contiguous(Row, [1, 2]));
    }

    [Fact]
    public void OnlyTheMonitorsBetweenComeAlong()
    {
        Rectangle[] four = [.. Row, new Rectangle(3840, 0, 1920, 1080)];
        Assert.Equal([0, 1, 2], MonitorSpan.Contiguous(four, [1, 2]));
        Assert.Equal([0, 2, 3], MonitorSpan.Contiguous(four, [0, 3]));
    }

    [Fact]
    public void NothingChosenOrNothingHereIsEveryMonitor()
    {
        Assert.Equal([0, 1, 2], MonitorSpan.Contiguous(Row, []));
        Assert.Equal([0, 1, 2], MonitorSpan.Contiguous(Row, [7]));
    }

    // Two monitors that meet only at a corner, the third beside one of them: no rectangle around the two holds the third.
    [Fact]
    public void MonitorsThatCannotBeJoinedGiveEveryMonitor()
    {
        Rectangle[] corner = [new(0, 0, 1920, 1080), new(1920, 1080, 1920, 1080), new(-1920, 0, 1920, 1080)];
        Assert.Equal([0, 1, 2], MonitorSpan.Contiguous(corner, [0, 1]));
    }

    [Fact]
    public void MonitorsShareAnEdgeNotJustACorner()
    {
        Assert.True(MonitorSpan.Adjacent(Middle, Right));
        Assert.True(MonitorSpan.Adjacent(Middle, new Rectangle(200, 1080, 1280, 1024))); // below, offset
        Assert.False(MonitorSpan.Adjacent(Middle, new Rectangle(1920, 1080, 1920, 1080))); // corner to corner
        Assert.False(MonitorSpan.Adjacent(Left, Right));
    }
}
