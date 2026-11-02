using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// open_view closes the tabs it opened itself beyond the last N (Revit memory during long agent sessions),
/// never the user's own tabs.
/// </summary>
public class OpenedViewsPolicyTests
{
    private static string Doc() => "doc-" + Guid.NewGuid();

    [Fact]
    public void Keeps_the_last_n_and_closes_the_older_ones_oldest_first()
    {
        var d = Doc();
        Assert.Empty(OpenedViewsPolicy.Activated(d, 1, false, 3));
        Assert.Empty(OpenedViewsPolicy.Activated(d, 2, false, 3));
        Assert.Empty(OpenedViewsPolicy.Activated(d, 3, false, 3));
        Assert.Equal(new long[] { 1 }, OpenedViewsPolicy.Activated(d, 4, false, 3));
        Assert.Equal(new long[] { 4, 3, 2 }, OpenedViewsPolicy.Tracked(d));
    }

    [Fact]
    public void A_view_the_user_already_had_open_is_never_tracked()
    {
        var d = Doc();
        OpenedViewsPolicy.Activated(d, 100, true, 2);      // user's tab
        OpenedViewsPolicy.Activated(d, 1, false, 2);
        OpenedViewsPolicy.Activated(d, 2, false, 2);
        Assert.Equal(new long[] { 1 }, OpenedViewsPolicy.Activated(d, 3, false, 2));
        Assert.DoesNotContain(100L, OpenedViewsPolicy.Tracked(d));
    }

    [Fact]
    public void Reopening_a_tracked_view_moves_it_to_the_front()
    {
        var d = Doc();
        OpenedViewsPolicy.Activated(d, 1, false, 2);
        OpenedViewsPolicy.Activated(d, 2, false, 2);
        Assert.Empty(OpenedViewsPolicy.Activated(d, 1, true, 2));   // still open, ours
        Assert.Equal(new long[] { 2 }, OpenedViewsPolicy.Activated(d, 3, false, 2));
        Assert.Equal(new long[] { 3, 1 }, OpenedViewsPolicy.Tracked(d));
    }

    [Fact]
    public void Keep_zero_closes_nothing_and_documents_are_separate()
    {
        var a = Doc();
        var b = Doc();
        for (long i = 1; i <= 5; i++) Assert.Empty(OpenedViewsPolicy.Activated(a, i, false, 0));
        OpenedViewsPolicy.Activated(b, 9, false, 1);
        Assert.Equal(new long[] { 9 }, OpenedViewsPolicy.Activated(b, 10, false, 1));
        Assert.Equal(5, OpenedViewsPolicy.Tracked(a).Count);
        OpenedViewsPolicy.Reset(a);
        Assert.Empty(OpenedViewsPolicy.Tracked(a));
    }
}
