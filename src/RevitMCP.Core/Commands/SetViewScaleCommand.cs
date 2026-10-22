using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>set_view_scale(doc, view, view_ids, scale)</c>.
///
/// Params: view_ids (ids, required), scale (int, required; 1:scale).
/// Returns a list, one row per id, in order: { id, scale } or { id, error } (error text cut at 80
/// chars) — a view that refuses the scale does not stop the others, like the route.
/// </summary>
public sealed class SetViewScaleCommand : IRevitCommand
{
    public string Name => "set_view_scale";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var arr = P.Arr(p, "view_ids");
        var scaleNode = p["scale"] ?? throw new RevitCommandException(
            "bad_request", "Missing required parameter 'scale'.");
        var scale = RouteCompat.IntTrunc(scaleNode, "scale");

        var outArr = new JsonArray();
        for (var i = 0; i < arr.Count; i++)
        {
            var vid = arr[i];
            try
            {
                var v = doc.GetElement(new ElementId(P.LongFrom(vid, $"view_ids[{i}]"))) as View
                    ?? throw new InvalidOperationException("vista nao encontrada");
                v.Scale = scale;
                outArr.Add(new JsonObject { ["id"] = RouteCompat.Echo(vid), ["scale"] = v.Scale });
            }
            catch (Exception ex)
            {
                outArr.Add(new JsonObject
                {
                    ["id"] = RouteCompat.Echo(vid),
                    ["error"] = RouteCompat.Trunc(ex.Message, 80),
                });
            }
        }
        return outArr;
    }
}
