using System;
using System.IO;
using System.Text.Json;

namespace RevitMCPAddin.Panel;

/// <summary>
/// What the add-in puts on the Revit ribbon.
/// <para><c>Tabs</c>: create the ribbon tabs/buttons ("AutoAudit", and the extra pane's tab).</para>
/// <para><c>AutoAudit</c>: register the AutoAudit dockable pane at all.</para>
/// Neither switch touches the MCP HTTP server, nor the extra dockable pane itself — the extra
/// pane (revit-mcp-extra-panel.json) is always registered when configured, with or without a tab,
/// so code that shows it by id (<see cref="ExtraPaneProvider.PaneId"/>) keeps working.
/// </summary>
internal sealed record RibbonSettings(bool Tabs, bool AutoAudit);

/// <summary>
/// Ribbon switches. Default (nothing configured) = <b>no ribbon tabs and no AutoAudit pane</b>.
/// Resolution order for <c>Tabs</c>:
/// <list type="number">
/// <item>env var <c>REVIT_MCP_ABAS</c> (1/true/sim/yes/on or 0/false/nao/no/off), read when Revit starts;</item>
/// <item><c>"abas"</c> in <c>revit-mcp-extra-panel.json</c>;</item>
/// <item><c>"abas"</c> in <c>revit-mcp-panel.json</c>;</item>
/// <item>false.</item>
/// </list>
/// <c>AutoAudit</c>: <c>"autoaudit"</c> in <c>revit-mcp-panel.json</c>, then in
/// <c>revit-mcp-extra-panel.json</c>; when absent it follows <c>Tabs</c> (so <c>"abas": true</c>
/// restores the upstream behaviour exactly: AutoAudit tab + pane, and the extra pane's tab).
/// Any read/parse failure falls back to the default — config can never take Revit down.
/// </summary>
internal static class RibbonConfig
{
    internal const string EnvVar = "REVIT_MCP_ABAS";

    internal static RibbonSettings Resolve(string revitVersion)
    {
        string? extraJson = null, panelJson = null;
        try
        {
            var extraPath = ExtraPanelConfig.ConfigPath(revitVersion);
            if (File.Exists(extraPath)) extraJson = File.ReadAllText(extraPath);
        }
        catch { /* unreadable = absent */ }
        try
        {
            var panelPath = PanelConfig.ConfigPath(revitVersion);
            if (File.Exists(panelPath)) panelJson = File.ReadAllText(panelPath);
        }
        catch { /* unreadable = absent */ }

        string? env = null;
        try { env = Environment.GetEnvironmentVariable(EnvVar); } catch { }

        return Resolve(env, extraJson, panelJson);
    }

    /// <summary>Pure resolution (unit-tested): env value and the two config files' text.</summary>
    internal static RibbonSettings Resolve(string? envValue, string? extraPanelJson, string? panelJson)
    {
        var tabs = ParseFlag(envValue)
                   ?? ReadFlag(extraPanelJson, "abas")
                   ?? ReadFlag(panelJson, "abas")
                   ?? false;
        var autoAudit = ReadFlag(panelJson, "autoaudit")
                        ?? ReadFlag(extraPanelJson, "autoaudit")
                        ?? tabs;
        return new RibbonSettings(tabs, autoAudit);
    }

    private static bool? ReadFlag(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(key, out var el))
                return null;
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => ParseFlag(el.GetString()),
                JsonValueKind.Number => el.TryGetInt32(out var n) ? n != 0 : null,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    internal static bool? ParseFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "sim": case "s": case "yes": case "y": case "on":
                return true;
            case "0": case "false": case "nao": case "não": case "n": case "no": case "off":
                return false;
            default:
                return null;
        }
    }
}
