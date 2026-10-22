using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>rooms_bbox(doc, view, level_name, nomes_contem=None)</c> (read-only).
///
/// Params: level_name (string, required: exact level name), nomes_contem (list of strings,
/// optional: keep rooms whose name contains any of them, case-insensitive).
/// Placed rooms only (Area &gt; 0). Returns [{ id, nome, min: [x, y], max: [x, y] }] in metres
/// (3 decimals), in collector order like the route.
/// </summary>
public sealed class RoomsBboxCommand : IRevitCommand
{
    public string Name => "rooms_bbox";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var levelName = P.Str(p, "level_name");
        var contem = RouteCompat.StrListOrNull(p, "nomes_contem");

        var outArr = new JsonArray();
        foreach (var rm in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType()
                     .OfType<Room>())
        {
            if (rm.Area <= 0 || rm.Level is null || rm.Level.Name != levelName) continue;
            var nm = rm.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            if (!RouteCompat.NameMatchesAny(nm, contem)) continue;
            var bb = rm.get_BoundingBox(null);
            if (bb is null) continue;
            outArr.Add(new JsonObject
            {
                ["id"] = rm.Id.Value,
                ["nome"] = nm,
                ["min"] = new JsonArray { RouteCompat.R(bb.Min.X * 0.3048, 3), RouteCompat.R(bb.Min.Y * 0.3048, 3) },
                ["max"] = new JsonArray { RouteCompat.R(bb.Max.X * 0.3048, 3), RouteCompat.R(bb.Max.Y * 0.3048, 3) },
            });
        }
        return outArr;
    }
}
