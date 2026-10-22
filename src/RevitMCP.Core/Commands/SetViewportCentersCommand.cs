using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>set_viewport_centers(doc, view, items)</c>.
///
/// Params: items = [{ id: viewport id, x: m, y: m }] (sheet coordinates, metres).
/// Viewport.SetBoxCenter (centre of the drawing, without the title); regenerates at the end.
/// Returns [{ id, ok: true }]. Any failure throws (the whole call rolls back), like the route.
/// </summary>
public sealed class SetViewportCentersCommand : IRevitCommand
{
    public string Name => "set_viewport_centers";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var items = RouteCompat.Items(ctx.Parameters, "items");

        var outArr = new JsonArray();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var label = $"items[{i}]";
            var idNode = RouteCompat.Req(it, "id", label);
            var id = P.LongFrom(idNode, $"{label}.id");
            var x = P.DblFrom(RouteCompat.Req(it, "x", label), $"{label}.x");
            var y = P.DblFrom(RouteCompat.Req(it, "y", label), $"{label}.y");

            var vp = doc.GetElement(new ElementId(id)) as Viewport
                ?? throw new RevitCommandException("not_found", $"Viewport {id} not found.");
            vp.SetBoxCenter(new XYZ(RouteCompat.ToFeet(x), RouteCompat.ToFeet(y), 0));
            outArr.Add(new JsonObject { ["id"] = RouteCompat.Echo(idNode), ["ok"] = true });
        }
        doc.Regenerate();
        return outArr;
    }
}
