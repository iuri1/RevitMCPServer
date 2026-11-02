namespace RevitMCPAddin.Commands;

/// <summary>
/// Which tabs open_view may close (pure, tested without Revit).
///
/// Every open view keeps its graphics in memory, and an agent working through the night opens hundreds of
/// them: Revit's memory grows until the machine struggles. The policy remembers, per document, the views
/// that open_view itself opened (most recent first) and asks to close the ones beyond <c>keep</c>.
/// A view that was already open when the agent activated it belongs to the user and is never tracked,
/// so the user's own tabs are never closed.
/// </summary>
public static class OpenedViewsPolicy
{
    public const int DefaultKeep = 3;

    private static readonly Dictionary<string, List<long>> _opened = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Records that <paramref name="viewId"/> was just activated and returns the ids to close
    /// (oldest first). <paramref name="keep"/> counts the active view; keep &lt;= 0 closes nothing.
    /// </summary>
    public static IReadOnlyList<long> Activated(string docKey, long viewId, bool wasAlreadyOpen, int keep)
    {
        lock (_lock)
        {
            if (!_opened.TryGetValue(docKey, out var mru))
                _opened[docKey] = mru = new List<long>();

            var tracked = mru.Remove(viewId);
            if (tracked || !wasAlreadyOpen)
                mru.Insert(0, viewId);

            if (keep <= 0 || mru.Count <= keep)
                return Array.Empty<long>();

            var close = mru.Skip(keep).Where(id => id != viewId).Reverse().ToList();
            mru.RemoveRange(keep, mru.Count - keep);
            return close;
        }
    }

    /// <summary>Forgets a document (closed or reloaded).</summary>
    public static void Reset(string docKey)
    {
        lock (_lock) _opened.Remove(docKey);
    }

    /// <summary>Views currently tracked for a document, most recent first (tests and diagnostics).</summary>
    public static IReadOnlyList<long> Tracked(string docKey)
    {
        lock (_lock)
            return _opened.TryGetValue(docKey, out var mru) ? mru.ToList() : new List<long>();
    }
}
