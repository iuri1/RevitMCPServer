using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>viewport_boxes(doc, view, sheet_id)</c> (read-only).
///
/// Params: sheet_id (id, required).
/// Returns, for every viewport on the sheet, sorted by box_m.min x:
///   { id, view (name), view_id, box_m: { min: [x, y], max: [x, y] }, w_m, h_m, center_m: [x, y] }
/// GetBoxOutline = the drawing only (no title); metres, box/centre 3 decimals, w/h 4 decimals.
/// </summary>
public sealed class ViewportBoxesCommand : IRevitCommand
{
    public string Name => "viewport_boxes";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var sheetId = P.Long(ctx.Parameters, "sheet_id");
        var sheet = doc.GetElement(new ElementId(sheetId))
            ?? throw new RevitCommandException("not_found", $"Sheet {sheetId} not found.");

        var rows = new List<(double MinX, JsonObject Row)>();
        foreach (var vp in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>())
        {
            var ol = vp.GetBoxOutline();
            var c = vp.GetBoxCenter();
            var v = doc.GetElement(vp.ViewId);
            rows.Add((RouteCompat.M(ol.MinimumPoint.X), new JsonObject
            {
                ["id"] = vp.Id.Value,
                ["view"] = v?.Name,
                ["view_id"] = vp.ViewId.Value,
                ["box_m"] = new JsonObject
                {
                    ["min"] = RouteCompat.Xy(ol.MinimumPoint.X, ol.MinimumPoint.Y),
                    ["max"] = RouteCompat.Xy(ol.MaximumPoint.X, ol.MaximumPoint.Y),
                },
                ["w_m"] = RouteCompat.R((ol.MaximumPoint.X - ol.MinimumPoint.X) * 0.3048, 4),
                ["h_m"] = RouteCompat.R((ol.MaximumPoint.Y - ol.MinimumPoint.Y) * 0.3048, 4),
                ["center_m"] = RouteCompat.Xy(c.X, c.Y),
            }));
        }

        var outArr = new JsonArray();
        foreach (var r in rows.OrderBy(r => r.MinX)) outArr.Add(r.Row);   // stable, like Python's sort
        return outArr;
    }
}
