using System.Collections.Generic;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// The commands ported from the JRTools routes keep the route names (bis_comum maps a route to the
/// command of the same name) and the right transaction policy: reads never open a transaction,
/// writes always do (that is also what makes 'tx_seguro' applicable to them).
/// </summary>
public class PortedRoutesRegistryTests
{
    public static IEnumerable<object[]> Ported() => new[]
    {
        new object[] { new CreateDraftingViewCommand(), "create_drafting_view", false },
        new object[] { new SetViewScaleCommand(), "set_view_scale", false },
        new object[] { new SetViewportCentersCommand(), "set_viewport_centers", false },
        new object[] { new ViewportBoxesCommand(), "viewport_boxes", true },
        new object[] { new PlaceScheduleOnSheetCommand(), "place_schedule_on_sheet", false },
        new object[] { new RoomsBboxCommand(), "rooms_bbox", true },
        new object[] { new RoomAreasCommand(), "room_areas", true },
        new object[] { new ElementBboxMCommand(), "element_bbox_m", true },
        new object[] { new DimsListCommand(), "dims_list", true },
        new object[] { new SetAnnotationCropCommand(), "set_annotation_crop", false },
        new object[] { new RenameViewsCommand(), "rename_views", false },
        new object[] { new ListTextTypesCommand(), "list_text_types", true },
        new object[] { new ListFilledRegionTypesCommand(), "list_filled_region_types", true },
    };

    [Theory]
    [MemberData(nameof(Ported))]
    public void Name_and_transaction_policy_match_the_route(IRevitCommand cmd, string route, bool readOnly)
    {
        Assert.Equal(route, cmd.Name);
        Assert.Equal(readOnly, cmd.IsReadOnly);
        Assert.Equal(readOnly ? ExecutionKind.ReadOnly : ExecutionKind.ModelWrite, cmd.Execution);
        Assert.Equal(readOnly ? "read" : cmd.RiskLevel, cmd.RiskLevel);
        Assert.Contains(cmd.RiskLevel, new[] { "read", "low", "medium" });
        Assert.False(cmd.SuppressWarningsOnCommit);
    }

    [Fact]
    public void Ported_names_do_not_collide_with_each_other()
    {
        var reg = new CommandRegistry();
        foreach (var row in Ported()) reg.Register((IRevitCommand)row[0]);
        Assert.Equal(13, reg.Count);
    }
}
