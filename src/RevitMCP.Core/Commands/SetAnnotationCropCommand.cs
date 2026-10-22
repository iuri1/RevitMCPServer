using System;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>set_annotation_crop(doc, view, view_ids, active=True, offset_m=0.0)</c>.
///
/// Params: view_ids (list, required), active (Python-truthy, default true), offset_m (metres,
/// default 0; only applied when active and non-zero).
/// Sets VIEWER_ANNOTATION_CROP_ACTIVE (when writable) and, with an offset, the four
/// ViewCropRegionShapeManager.*AnnotationCropOffset values (each one best-effort). Regenerates.
/// Returns [{ view, ok: true } | { view, error }] (error text cut at 80 chars), one per id.
/// </summary>
public sealed class SetAnnotationCropCommand : IRevitCommand
{
    public string Name => "set_annotation_crop";
    public bool IsReadOnly => false;
    public string RiskLevel => "medium";

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var arr = P.Arr(p, "view_ids");
        var active = RouteCompat.TruthyOr(p, "active", true);
        var offsetNode = p["offset_m"];
        var applyOffset = active && RouteCompat.Truthy(offsetNode);
        var offsetFt = applyOffset ? RouteCompat.ToFeet(P.DblFrom(offsetNode, "offset_m")) : 0.0;

        var outArr = new JsonArray();
        for (var i = 0; i < arr.Count; i++)
        {
            var vid = arr[i];
            try
            {
                var v = doc.GetElement(new ElementId(P.LongFrom(vid, $"view_ids[{i}]"))) as View
                    ?? throw new InvalidOperationException("vista nao encontrada");
                var prm = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (prm is not null && !prm.IsReadOnly) prm.Set(active ? 1 : 0);
                if (applyOffset)
                {
                    var mgr = v.GetCropRegionShapeManager();
                    TrySet(() => mgr.LeftAnnotationCropOffset = offsetFt);
                    TrySet(() => mgr.RightAnnotationCropOffset = offsetFt);
                    TrySet(() => mgr.TopAnnotationCropOffset = offsetFt);
                    TrySet(() => mgr.BottomAnnotationCropOffset = offsetFt);
                }
                outArr.Add(new JsonObject { ["view"] = RouteCompat.Echo(vid), ["ok"] = true });
            }
            catch (Exception ex)
            {
                outArr.Add(new JsonObject
                {
                    ["view"] = RouteCompat.Echo(vid),
                    ["error"] = RouteCompat.Trunc(ex.Message, 80),
                });
            }
        }
        doc.Regenerate();
        return outArr;
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch { /* route: setattr(...) inside try/except: pass */ }
    }
}
