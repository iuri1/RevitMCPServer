using System;
using System.IO;
using System.Text.Json;

namespace RevitMCPAddin.Panel;

/// <summary>
/// How to start the local service behind the extra pane when it is not answering yet
/// (jr fork). <c>File</c> is the executable (resolved through PATH when not a path),
/// <c>Args</c> its command line, <c>Cwd</c> the working directory (null = the add-in's).
/// </summary>
internal sealed record ServiceStart(string File, string Args, string? Cwd);

/// <summary>
/// What the optional second pane shows and how its ribbon entry is named. <c>Start</c> and
/// <c>Waiting</c> are optional: without them the pane is a plain browser onto <c>Url</c>.
/// </summary>
internal sealed record ExtraPanelSettings(
    string Url, string Tab, string Label, ServiceStart? Start = null, string? Waiting = null);

/// <summary>
/// Reads the optional second pane from
/// <c>%APPDATA%\Autodesk\Revit\Addins\{version}\revit-mcp-extra-panel.json</c>:
/// <code>
/// { "url": "http://127.0.0.1:9000/ui/", "tab": "My Tool", "label": "My Tool",
///   "start": { "file": "python", "args": "server.py --port 9000", "cwd": "C:\\tool" },
///   "waiting": "Starting My Tool…" }
/// </code>
/// Unlike <see cref="PanelConfig"/> (AutoAudit, always on), this pane is opt-in: no file, no
/// usable <c>url</c>, or <c>"enabled": false</c> means neither the pane nor its ribbon tab is
/// ever registered. The add-in ships nothing for the pane to show — it is only a browser onto
/// whatever local service the file names, so which tool sits behind it is the installing user's
/// business, not this repo's. <c>start</c> (jr fork) lets that service be launched by the add-in
/// when Revit opens, instead of the pane showing the browser's "can't connect" page until a
/// button somewhere else starts it.
/// </summary>
internal static class ExtraPanelConfig
{
    internal const string DefaultLabel = "Extra Panel";

    internal static string ConfigPath(string revitVersion) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Autodesk", "Revit", "Addins", revitVersion, "revit-mcp-extra-panel.json");

    /// <summary>Null means "do not register the pane".</summary>
    internal static ExtraPanelSettings? Resolve(string revitVersion)
    {
        try
        {
            var path = ConfigPath(revitVersion);
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path));
        }
        catch
        {
            // Corrupt config must never take the panel (or Revit) down.
            return null;
        }
    }

    /// <summary>Pure parsing (unit-tested). Null means "do not register the pane".</summary>
    internal static ExtraPanelSettings? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("enabled", out var enabled) &&
                enabled.ValueKind == JsonValueKind.False)
                return null;

            var url = ReadString(root, "url");
            if (url is null ||
                !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                return null;

            var label = ReadString(root, "label") ?? DefaultLabel;
            var tab = ReadString(root, "tab") ?? label;
            return new ExtraPanelSettings(url, tab, label, ReadStart(root), ReadString(root, "waiting"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>"start": {"file", "args", "cwd"}</c>. A start block without a usable <c>file</c> is
    /// ignored (the pane still works as a plain browser) — never a reason to drop the pane.
    /// </summary>
    private static ServiceStart? ReadStart(JsonElement root)
    {
        if (!root.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object)
            return null;
        var file = ReadString(start, "file");
        if (file is null) return null;
        var cwd = ReadString(start, "cwd");
        if (cwd is not null && !Directory.Exists(cwd)) return null;   // folder moved/uninstalled: do not launch
        return new ServiceStart(file, ReadString(start, "args") ?? string.Empty, cwd);
    }

    private static string? ReadString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }
}
