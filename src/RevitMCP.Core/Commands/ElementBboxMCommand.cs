using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>element_bbox_m(doc, view, ids)</c> (read-only).
///
/// Params: ids (list, required).
/// Returns an object keyed by id text: { "123": { min: [x,y,z], max: [x,y,z], wall_normal_deg? } | null }
/// in model coordinates, metres (3 decimals). wall_normal_deg comes from the wall itself or from
/// the host wall of a hosted instance (door/window): degrees(atan2(-t.X, t.Y)) of the location
/// curve tangent. null = element without a bounding box. A missing element is an error (the
/// route raises too).
/// </summary>
public sealed class ElementBboxMCommand : IRevitCommand
{
    public string Name => "element_bbox_m";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var outObj = new JsonObject();
        foreach (var id in RouteCompat.Ids(ctx.Parameters, "ids"))
        {
            var el = doc.GetElement(new ElementId(id))
                ?? throw new RevitCommandException("not_found", $"Element {id} not found.");
            var key = RouteCompat.IdKey(id);
            var bb = el.get_BoundingBox(null);
            if (bb is null)
            {
                outObj[key] = null;
                continue;
            }
            var row = new JsonObject
            {
                ["min"] = RouteCompat.Xyz(bb.Min.X, bb.Min.Y, bb.Min.Z),
                ["max"] = RouteCompat.Xyz(bb.Max.X, bb.Max.Y, bb.Max.Z),
            };
            var w = el as Wall ?? (el as FamilyInstance)?.Host;
            if (w?.Location is LocationCurve lc && lc.Curve is { } c)
            {
                try
                {
                    var t = c.GetEndPoint(1) - c.GetEndPoint(0);
                    row["wall_normal_deg"] = RouteCompat.WallNormalDeg(t.X, t.Y);
                }
                catch
                {
                    // route: except Exception: pass
                }
            }
            outObj[key] = row;
        }
        return outObj;
    }
}
