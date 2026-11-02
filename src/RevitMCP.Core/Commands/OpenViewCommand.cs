using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Switch the active view to a specified view.
///
/// Params:
///   - viewId: long, required
///   - keepOpen: int, optional (default 3) — tabs opened by open_view to keep, counting this one; older ones
///     are closed so a long agent session does not pile up open views (Revit memory). Views the user already
///     had open are never closed. 0 = never close.
/// </summary>
public sealed class OpenViewCommand : IRevitCommand
{
    public string Name => "open_view";
    public bool IsReadOnly => false;
    public ExecutionKind Execution => ExecutionKind.UiAction;

    public JsonNode? Execute(CommandContext ctx)
    {
        var uiDoc = ctx.RequireUIDoc();
        var doc = ctx.RequireDoc();
        var viewId = new ElementId(P.Long(ctx.Parameters, "viewId"));
        var keep = P.IntOr(ctx.Parameters, "keepOpen", OpenedViewsPolicy.DefaultKeep);

        var view = doc.GetElement(viewId) as View
            ?? throw new RevitCommandException("not_found", $"View {viewId.Value} not found.");

        bool wasOpen;
        var before = uiDoc.GetOpenUIViews();
        try { wasOpen = before.Any(u => u.ViewId == view.Id); }
        finally { foreach (var u in before) u.Dispose(); }

        uiDoc.ActiveView = view;

        var docKey = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
        var toClose = OpenedViewsPolicy.Activated(docKey, view.Id.Value, wasOpen, keep);
        var closed = new JsonArray();
        if (toClose.Count > 0)
        {
            var active = uiDoc.ActiveView?.Id.Value;
            var open = uiDoc.GetOpenUIViews();
            try
            {
                foreach (var u in open)
                {
                    // the id must be read BEFORE Close(): afterwards the UIView answers -1
                    var id = u.ViewId.Value;
                    if (!toClose.Contains(id) || id == active) continue;
                    var name = (doc.GetElement(new ElementId(id)) as View)?.Name;
                    try { u.Close(); closed.Add(new JsonObject { ["viewId"] = id, ["name"] = name }); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { /* last open view or busy: leave it */ }
                }
            }
            finally { foreach (var u in open) u.Dispose(); }
        }

        return new JsonObject
        {
            ["viewId"] = view.Id.Value,
            ["name"] = view.Name,
            ["viewType"] = view.ViewType.ToString(),
            ["closedViews"] = closed,
        };
    }
}
