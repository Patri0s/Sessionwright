using System.Drawing;

namespace Sessionwright;

/// <summary>
/// Which monitors a full screen on several of them can use. Remote Desktop takes only monitors that lie next to each
/// other ("selectedmonitors": the selected displays must be contiguous): the left and the right one of three gave a
/// stretched, broken picture, the window lying over the middle one as well. The monitors between the chosen ones are
/// then used too.
/// </summary>
static class MonitorSpan
{
    /// <summary>
    /// The monitors to use, by their place in <paramref name="all"/>, in that order: <paramref name="chosen"/> when
    /// they lie next to each other, else with those between them (what their common rectangle touches), else all.
    /// </summary>
    internal static int[] Contiguous(IReadOnlyList<Rectangle> all, IReadOnlyList<int> chosen)
    {
        var picked = chosen.Where(i => i >= 0 && i < all.Count).Distinct().Order().ToArray();
        if (picked.Length == 0) return [.. Enumerable.Range(0, all.Count)];
        if (Connected(all, picked)) return picked;
        var span = picked.Select(i => all[i]).Aggregate(Rectangle.Union);
        var filled = Enumerable.Range(0, all.Count).Where(i => picked.Contains(i) || Overlap(all[i], span)).ToArray();
        return Connected(all, filled) ? filled : [.. Enumerable.Range(0, all.Count)];
    }

    /// <summary>Every monitor can be reached from the first over monitors that share an edge.</summary>
    internal static bool Connected(IReadOnlyList<Rectangle> all, IReadOnlyList<int> monitors)
    {
        if (monitors.Count < 2) return true;
        var reached = new HashSet<int> { monitors[0] };
        var queue = new Queue<int>(reached);
        while (queue.Count > 0)
        {
            var from = queue.Dequeue();
            foreach (var to in monitors)
                if (!reached.Contains(to) && Adjacent(all[from], all[to]) && reached.Add(to)) queue.Enqueue(to);
        }
        return reached.Count == monitors.Count;
    }

    /// <summary>Side by side or one above the other along a piece of an edge (a shared corner alone is not), or overlapping.</summary>
    internal static bool Adjacent(Rectangle a, Rectangle b)
    {
        var across = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var down = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return across == 0 && down > 0 || down == 0 && across > 0 || across > 0 && down > 0;
    }

    static bool Overlap(Rectangle a, Rectangle b)
    {
        var both = Rectangle.Intersect(a, b);
        return both.Width > 0 && both.Height > 0;
    }
}
