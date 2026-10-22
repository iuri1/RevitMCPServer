using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>create_drafting_view(doc, view, name, scale=10)</c>.
///
/// Params (same names as the route): name (string, required), scale (int, default 10).
/// Returns the route's payload: { id, name, scale }, or { error: "sem ViewFamilyType Drafting" }
/// when the project has no drafting view type (data, not an error envelope — like the route).
/// A name Revit rejects (duplicate, illegal characters) throws → rollback + error envelope.
/// </summary>
public sealed class CreateDraftingViewCommand : IRevitCommand
{
    public string Name => "create_drafting_view";
    public bool IsReadOnly => false;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var p = ctx.Parameters;
        var name = P.Str(p, "name");
        var scale = p["scale"] is { } s ? RouteCompat.IntTrunc(s, "scale") : 10;

        var vft = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting);
        if (vft is null)
            return new JsonObject { ["error"] = "sem ViewFamilyType Drafting" };

        var dv = ViewDrafting.Create(doc, vft.Id);
        dv.Name = name;
        dv.Scale = scale;
        return new JsonObject { ["id"] = dv.Id.Value, ["name"] = dv.Name, ["scale"] = dv.Scale };
    }
}
