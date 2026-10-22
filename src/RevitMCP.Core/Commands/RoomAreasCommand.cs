using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>room_areas(doc, view, ids=None)</c> (read-only).
///
/// Params: ids (list, optional; empty/absent = every element of the Rooms category, placed or not).
/// Returns [{ id, nome, area_m2 (2 decimals), nivel }]. Like the route, ids that do not exist and
/// elements the route could not read (no room name parameter, no level element) are skipped.
/// </summary>
public sealed class RoomAreasCommand : IRevitCommand
{
    public string Name => "room_areas";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;

        IEnumerable<Element?> els;
        if (RouteCompat.Truthy(p["ids"]))
        {
            var list = new List<Element?>();
            foreach (var id in RouteCompat.Ids(p, "ids")) list.Add(doc.GetElement(new ElementId(id)));
            els = list;
        }
        else
        {
            els = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).ToElements();
        }

        var outArr = new JsonArray();
        foreach (var e in els)
        {
            if (e is not SpatialElement r) continue;
            var nameParam = r.get_Parameter(BuiltInParameter.ROOM_NAME);
            if (nameParam is null) continue;
            string? nivel = null;
            if (r.LevelId is not null)
            {
                var lv = doc.GetElement(r.LevelId);
                if (lv is null) continue;            // route: doc.GetElement(None).Name raises -> skipped
                nivel = lv.Name;
            }
            outArr.Add(new JsonObject
            {
                ["id"] = r.Id.Value,
                ["nome"] = nameParam.AsString(),
                ["area_m2"] = RouteCompat.SqFeetToM2(r.Area),
                ["nivel"] = nivel,
            });
        }
        return outArr;
    }
}
