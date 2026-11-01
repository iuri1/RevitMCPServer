using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Port of the JRTools route <c>list_text_types(doc, view)</c> (read-only).
///
/// No params. Returns [{ id, name, size_mm (TEXT_SIZE in mm, 2 decimals; null when 0/unknown),
/// color (LINE_COLOR integer, null when unknown) }] sorted by name (code-point order, as Python).
/// </summary>
public sealed class ListTextTypesCommand : IRevitCommand
{
    public string Name => "list_text_types";
    public bool IsReadOnly => true;

    public JsonNode? Execute(CommandContext ctx)
    {
        var doc = ctx.RequireDoc();
        var rows = new List<(string? Name, JsonObject Row)>();
        foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>())
        {
            double? size = null;
            try { size = t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble(); } catch { }
            int? color = null;
            try { color = t.get_Parameter(BuiltInParameter.LINE_COLOR)?.AsInteger(); } catch { }
            var name = t.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString();
            rows.Add((name, new JsonObject
            {
                ["id"] = t.Id.Value,
                ["name"] = name,
                ["size_mm"] = RouteCompat.TextSizeMm(size),
                ["color"] = color,
            }));
        }
        var outArr = new JsonArray();
        foreach (var r in rows.OrderBy(r => r.Name, RouteCompat.PySort)) outArr.Add(r.Row);
        return outArr;
    }
}
