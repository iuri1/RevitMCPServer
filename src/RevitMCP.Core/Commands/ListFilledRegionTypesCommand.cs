using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>list_filled_region_types(doc, view)</c> (read-only).
///
/// No params. Returns the sorted list of FilledRegionType names (strings only, like the route).
/// </summary>
public sealed class ListFilledRegionTypesCommand : IRevitCommand
{
    public string Name => "list_filled_region_types";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var names = new FilteredElementCollector(doc)
            .OfClass(typeof(FilledRegionType))
            .Cast<FilledRegionType>()
            .Select(ft => ft.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString())
            .OrderBy(n => n, RouteCompat.PySort);
        var outArr = new JsonArray();
        foreach (var n in names) outArr.Add(n);
        return outArr;
    }
}
