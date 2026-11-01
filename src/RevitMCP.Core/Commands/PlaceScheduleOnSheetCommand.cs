using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>place_schedule_on_sheet(doc, view, sheet_id, schedule_id, x_m, y_m)</c>.
///
/// Params: sheet_id, schedule_id (ids), x_m, y_m (metres on the sheet: the origin
/// ScheduleSheetInstance.Create takes, i.e. the upper-left corner of the schedule).
/// Returns { id, bbox_m: { min: [x, y], max: [x, y] } | null } (bounding box in the sheet).
/// </summary>
public sealed class PlaceScheduleOnSheetCommand : IRevitCommand
{
    public string Name => "place_schedule_on_sheet";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var sheetId = new ElementId(P.Long(p, "sheet_id"));
        var scheduleId = new ElementId(P.Long(p, "schedule_id"));
        var x = P.Dbl(p, "x_m");
        var y = P.Dbl(p, "y_m");

        var inst = ScheduleSheetInstance.Create(doc, sheetId, scheduleId,
            new XYZ(RouteCompat.ToFeet(x), RouteCompat.ToFeet(y), 0));
        doc.Regenerate();
        var sheet = doc.GetElement(sheetId) as View;
        var bb = inst.get_BoundingBox(sheet);
        return new JsonObject
        {
            ["id"] = inst.Id.Value,
            ["bbox_m"] = bb is null ? null : new JsonObject
            {
                ["min"] = RouteCompat.Xy(bb.Min.X, bb.Min.Y),
                ["max"] = RouteCompat.Xy(bb.Max.X, bb.Max.Y),
            },
        };
    }
}
