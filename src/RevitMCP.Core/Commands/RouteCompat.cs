using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Pure helpers (no Revit types) shared by the commands ported from the JRTools pyRevit routes
/// (Rotas.pushbutton/script.py). They reproduce what the IronPython code does so the C# payload is
/// the SAME JSON the route returned and callers can switch without changes:
///   - units: the route works in metres outside and feet inside (<c>_FT = 1/0.3048</c>,
///     <c>_m(v) = round(v / _FT, 3)</c>);
///   - rounding: Python 2 <c>round()</c> rounds halves away from zero (C#'s default is banker's);
///   - Python truthiness for flags such as <c>active</c> / <c>offset_m</c>;
///   - <c>str(ex)[:n]</c> truncation of error texts, echo of the caller's id values.
/// </summary>
public static class RouteCompat
{
    /// <summary>Feet per metre, exactly as the route's <c>_FT</c>.</summary>
    public const double Ft = 1.0 / 0.3048;

    /// <summary>Python 2 <c>round(v, digits)</c> (halves away from zero).</summary>
    public static double R(double v, int digits) =>
        Math.Round(v, digits, MidpointRounding.AwayFromZero);

    /// <summary>The route's <c>_m(v)</c>: internal feet → metres, 3 decimals.</summary>
    public static double M(double feet) => R(feet / Ft, 3);

    /// <summary>Metres → internal feet as the route does it (<c>float(x) * _FT</c>).</summary>
    public static double ToFeet(double metres) => metres * Ft;

    /// <summary>[_m(x), _m(y)] — the route's <c>_xyz_m(p)[:2]</c>.</summary>
    public static JsonArray Xy(double xFt, double yFt) => new() { M(xFt), M(yFt) };

    /// <summary>[_m(x), _m(y), _m(z)].</summary>
    public static JsonArray Xyz(double xFt, double yFt, double zFt) => new() { M(xFt), M(yFt), M(zFt) };

    /// <summary>Python <c>str(ex)[:n]</c>.</summary>
    public static string Trunc(string? s, int n)
    {
        s ??= string.Empty;
        return s.Length <= n ? s : s.Substring(0, n);
    }

    /// <summary>
    /// Python truthiness of a JSON value: null/false/0/""/[]/{} are false, everything else true
    /// (so the string "false" is TRUE, exactly like the route).
    /// </summary>
    public static bool Truthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonArray a:
                return a.Count > 0;
            case JsonObject o:
                return o.Count > 0;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case System.Text.Json.JsonValueKind.True: return true;
                    case System.Text.Json.JsonValueKind.False:
                    case System.Text.Json.JsonValueKind.Null: return false;
                    case System.Text.Json.JsonValueKind.Number:
                        // works for parsed values and for int/long/double-backed ones built in code
                        return double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) != 0.0;
                    case System.Text.Json.JsonValueKind.String:
                        return v.GetValue<string>().Length > 0;
                    default: return true;
                }
            default:
                return true;
        }
    }

    /// <summary>Optional flag with a default, Python-truthy (route signature <c>active=True</c>).</summary>
    public static bool TruthyOr(JsonObject p, string key, bool @default) =>
        p.ContainsKey(key) ? Truthy(p[key]) : @default;

    /// <summary>
    /// Python <c>int(x)</c> for a scale or similar: truncates toward zero; numeric strings accepted.
    /// </summary>
    public static int IntTrunc(JsonNode? node, string label)
    {
        var d = P.DblFrom(node, label);
        var t = Math.Truncate(d);
        if (t < int.MinValue || t > int.MaxValue)
            throw new RevitCommandException("invalid_parameter", $"'{label}' is out of range: {d}.");
        return (int)t;
    }

    /// <summary>Key the route uses for <c>out[str(i)]</c> when ids arrive as ints: "123".</summary>
    public static string IdKey(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Echo of a caller value (the route returns <c>{'id': vid}</c> with what it received).</summary>
    public static JsonNode? Echo(JsonNode? node) => node?.DeepClone();

    /// <summary>
    /// Names tried by <c>rename_views</c> for one item: "nome", "nome (2)", ..., "nome (20)" —
    /// the route stops after 20 attempts.
    /// </summary>
    public static IEnumerable<string> RenameCandidates(string nome)
    {
        yield return nome;
        for (var k = 2; k <= 20; k++)
            yield return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", nome, k);
    }

    /// <summary>rooms_bbox filter: true when no filter or the name contains any key (case-insensitive).</summary>
    public static bool NameMatchesAny(string? name, IReadOnlyCollection<string>? contains)
    {
        if (contains is null || contains.Count == 0) return true;
        var nm = (name ?? string.Empty).ToLowerInvariant();
        return contains.Any(k => nm.Contains((k ?? string.Empty).ToLowerInvariant()));
    }

    /// <summary>element_bbox_m: <c>round(degrees(atan2(-t.X, t.Y)), 3)</c> from the curve tangent.</summary>
    public static double WallNormalDeg(double tx, double ty) =>
        R(Math.Atan2(-tx, ty) * 180.0 / Math.PI, 3);

    /// <summary>dims_list: 'u' when the dimension runs along the view's right direction, else 'v'.</summary>
    public static string DimDir(double dotWithRight) => Math.Abs(dotWithRight) > 0.7 ? "u" : "v";

    /// <summary>dims_list: dimension value (feet) → cm, 1 decimal.</summary>
    public static double FeetToCm1(double feet) => R(feet * 30.48, 1);

    /// <summary>list_text_types: TEXT_SIZE (feet) → mm, 2 decimals; 0/absent → null (route: <c>if size</c>).</summary>
    public static double? TextSizeMm(double? sizeFeet)
    {
        if (sizeFeet is null) return null;
        var mm = sizeFeet.Value * 304.8;
        return mm == 0.0 ? null : R(mm, 2);
    }

    /// <summary>Room/area value (square feet) → m², 2 decimals (route: <c>round(a * 0.09290304, 2)</c>).</summary>
    public static double SqFeetToM2(double sqft) => R(sqft * 0.09290304, 2);

    /// <summary>Python 2 sort of unicode strings: by code point (ordinal); null first.</summary>
    public static readonly IComparer<string?> PySort = Comparer<string?>.Create(
        (a, b) => a is null ? (b is null ? 0 : -1) : b is null ? 1 : string.CompareOrdinal(a, b));

    /// <summary>Reads a list of ids (ints or numeric strings) as the route's <c>_new_id(int(i))</c>.</summary>
    public static List<long> Ids(JsonObject p, string key)
    {
        var arr = P.Arr(p, key);
        var ids = new List<long>(arr.Count);
        for (var i = 0; i < arr.Count; i++) ids.Add(P.LongFrom(arr[i], $"{key}[{i}]"));
        return ids;
    }

    /// <summary>Optional list of strings (null when absent/null). A bare string counts as one item.</summary>
    public static List<string>? StrListOrNull(JsonObject p, string key)
    {
        switch (p[key])
        {
            case null:
                return null;
            case JsonArray arr:
                var list = new List<string>(arr.Count);
                for (var i = 0; i < arr.Count; i++) list.Add(P.StrFrom(arr[i], $"{key}[{i}]"));
                return list;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return new List<string> { s };
            default:
                throw new RevitCommandException("invalid_parameter", $"Parameter '{key}' must be a list of strings.");
        }
    }

    /// <summary>The list of objects a route takes as <c>items</c>.</summary>
    public static List<JsonObject> Items(JsonObject p, string key)
    {
        var arr = P.Arr(p, key);
        var list = new List<JsonObject>(arr.Count);
        for (var i = 0; i < arr.Count; i++)
            list.Add(arr[i] as JsonObject
                ?? throw new RevitCommandException("invalid_parameter", $"'{key}[{i}]' must be an object."));
        return list;
    }

    /// <summary>A required key of one item, Python-style (<c>it['id']</c> → KeyError becomes bad_request).</summary>
    public static JsonNode Req(JsonObject item, string key, string label) =>
        item[key] ?? throw new RevitCommandException("bad_request", $"Missing '{key}' in {label}.");
}
