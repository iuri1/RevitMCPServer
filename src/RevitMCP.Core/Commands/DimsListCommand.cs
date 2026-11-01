using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>dims_list(doc, view, view_ids)</c> (read-only).
///
/// Params: view_ids (list, required).
/// Returns { "&lt;view id&gt;": [ { id, cm: [..] | null, dir: "u"|"v"|"?", uv?: [u, v] } ] } —
/// linear/angular/radial dimensions of each view (spot dimensions skipped), to find duplicates:
///   cm  = segment values (multi-segment) or [value], centimetres 1 decimal;
///   dir = "u" when the dimension line runs along the view's RightDirection (|dot| &gt; 0.7);
///   uv  = origin relative to the view origin along Right/Up, metres 2 decimals (omitted when
///         Revit cannot give an origin). Views that do not exist are left out.
/// </summary>
public sealed class DimsListCommand : IRevitCommand
{
    public string Name => "dims_list";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var outObj = new JsonObject();
        foreach (var vid in RouteCompat.Ids(ctx.Parameters, "view_ids"))
        {
            if (doc.GetElement(new ElementId(vid)) is not View v) continue;
            var right = v.RightDirection;
            var up = v.UpDirection;
            var org = v.Origin;

            var linhas = new JsonArray();
            foreach (var d in new FilteredElementCollector(doc, v.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                if (d is SpotDimension) continue;
                var it = new JsonObject { ["id"] = d.Id.Value };

                try
                {
                    var cm = new JsonArray();
                    if (d.NumberOfSegments > 1)
                    {
                        foreach (DimensionSegment sg in d.Segments)
                            cm.Add(RouteCompat.FeetToCm1(sg.Value ?? throw new System.InvalidOperationException()));
                    }
                    else
                    {
                        cm.Add(RouteCompat.FeetToCm1(d.Value ?? throw new System.InvalidOperationException()));
                    }
                    it["cm"] = cm;
                }
                catch
                {
                    it["cm"] = null;
                }

                try
                {
                    var line = d.Curve as Line ?? throw new System.InvalidOperationException();
                    it["dir"] = RouteCompat.DimDir(line.Direction.DotProduct(right));
                }
                catch
                {
                    it["dir"] = "?";
                }

                try
                {
                    var o = d.Origin - org;
                    it["uv"] = new JsonArray
                    {
                        RouteCompat.R(o.DotProduct(right) * 0.3048, 2),
                        RouteCompat.R(o.DotProduct(up) * 0.3048, 2),
                    };
                }
                catch
                {
                    // route: except Exception: pass
                }
                linhas.Add(it);
            }
            outObj[RouteCompat.IdKey(vid)] = linhas;
        }
        return outObj;
    }
}
