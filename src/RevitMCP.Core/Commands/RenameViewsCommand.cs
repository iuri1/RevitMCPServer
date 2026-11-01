using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>rename_views(doc, view, items)</c>.
///
/// Params: items = [{ id, nome }].
/// Tries "nome", then "nome (2)" ... "nome (20)" until Revit accepts the name (same rule as the
/// route; after 20 refusals the view keeps its name). Returns [{ id, nome }] with the name the
/// view ended up with. A missing view is an error (the route raises too).
/// </summary>
public sealed class RenameViewsCommand : IRevitCommand
{
    public string Name => "rename_views";
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
            var nome = P.StrFrom(RouteCompat.Req(it, "nome", label), $"{label}.nome");

            var v = doc.GetElement(new ElementId(id))
                ?? throw new RevitCommandException("not_found", $"View {id} not found.");
            foreach (var candidato in RouteCompat.RenameCandidates(nome))
            {
                try
                {
                    v.Name = candidato;
                    break;
                }
                catch
                {
                    // name taken / invalid: try the next suffix
                }
            }
            outArr.Add(new JsonObject { ["id"] = RouteCompat.Echo(idNode), ["nome"] = v.Name });
        }
        return outArr;
    }
}
