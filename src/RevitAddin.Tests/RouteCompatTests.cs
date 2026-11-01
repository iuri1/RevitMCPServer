using System.Linq;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// Helpers that make the ported commands return the same JSON as the JRTools IronPython routes:
/// units, Python 2 rounding/truthiness, argument coercion, naming rules. No Revit needed.
/// </summary>
public class RouteCompatTests
{
    // ── units / rounding ──────────────────────────────────────────────────

    [Fact]
    public void M_is_the_routes_underscore_m()
    {
        Assert.Equal(1.0, RouteCompat.M(1.0 / 0.3048));
        Assert.Equal(0.305, RouteCompat.M(1.0));          // 0.3048 -> 3 decimals
        Assert.Equal(-1.524, RouteCompat.M(-5.0));
    }

    [Fact]
    public void ToFeet_round_trips_with_M()
    {
        Assert.Equal(12.345, RouteCompat.M(RouteCompat.ToFeet(12.345)));
        Assert.Equal(1.0 / 0.3048, RouteCompat.ToFeet(1.0), 12);
    }

    [Fact]
    public void R_rounds_halves_away_from_zero_like_python2()
    {
        Assert.Equal(0.13, RouteCompat.R(0.125, 2));     // C# default (banker's) would give 0.12
        Assert.Equal(-0.13, RouteCompat.R(-0.125, 2));
        Assert.Equal(3.0, RouteCompat.R(2.5, 0));
    }

    [Fact]
    public void Area_and_text_size_conversions()
    {
        Assert.Equal(1.0, RouteCompat.SqFeetToM2(1.0 / 0.09290304));
        Assert.Equal(2.0, RouteCompat.TextSizeMm(2.0 / 304.8));
        Assert.Null(RouteCompat.TextSizeMm(0.0));         // route: `if size` -> None
        Assert.Null(RouteCompat.TextSizeMm(null));
        Assert.Equal(10.0, RouteCompat.FeetToCm1(10.0 / 30.48));
    }

    [Fact]
    public void Xy_and_Xyz_are_metre_arrays()
    {
        Assert.Equal("[1,2]", RouteCompat.Xy(1 / 0.3048, 2 / 0.3048).ToJsonString());
        Assert.Equal("[1,2,3]", RouteCompat.Xyz(1 / 0.3048, 2 / 0.3048, 3 / 0.3048).ToJsonString());
    }

    // ── Python truthiness / int() ─────────────────────────────────────────

    [Theory]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("0.0", false)]
    [InlineData("\"\"", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    [InlineData("true", true)]
    [InlineData("0.05", true)]
    [InlineData("\"false\"", true)]   // a non-empty string is truthy in Python
    [InlineData("[0]", true)]
    public void Truthy_follows_python(string json, bool expected)
    {
        var obj = JsonNode.Parse("{\"v\":" + json + "}")!.AsObject();
        Assert.Equal(expected, RouteCompat.Truthy(obj["v"]));
    }

    [Fact]
    public void TruthyOr_uses_default_only_when_key_is_absent()
    {
        Assert.True(RouteCompat.TruthyOr(new JsonObject(), "active", true));
        Assert.False(RouteCompat.TruthyOr(new JsonObject { ["active"] = null }, "active", true));
        Assert.False(RouteCompat.TruthyOr(new JsonObject { ["active"] = 0 }, "active", true));
    }

    [Theory]
    [InlineData("50", 50)]
    [InlineData("50.0", 50)]
    [InlineData("12.7", 12)]
    [InlineData("\"25\"", 25)]
    public void IntTrunc_is_python_int(string json, int expected)
    {
        var obj = JsonNode.Parse("{\"v\":" + json + "}")!.AsObject();
        Assert.Equal(expected, RouteCompat.IntTrunc(obj["v"], "scale"));
    }

    [Fact]
    public void IntTrunc_rejects_non_numbers()
    {
        var ex = Assert.Throws<RevitCommandException>(() => RouteCompat.IntTrunc(JsonValue.Create("x"), "scale"));
        Assert.Equal("invalid_parameter", ex.Code);
    }

    // ── argument readers ──────────────────────────────────────────────────

    [Fact]
    public void Ids_accepts_ints_and_numeric_strings()
    {
        var p = JsonNode.Parse("""{"ids":[1, "2", 3.0]}""")!.AsObject();
        Assert.Equal(new long[] { 1, 2, 3 }, RouteCompat.Ids(p, "ids"));
    }

    [Fact]
    public void Ids_missing_is_bad_request_and_bad_item_is_invalid_parameter()
    {
        Assert.Equal("bad_request",
            Assert.Throws<RevitCommandException>(() => RouteCompat.Ids(new JsonObject(), "ids")).Code);
        var p = JsonNode.Parse("""{"ids":[1, "x"]}""")!.AsObject();
        var ex = Assert.Throws<RevitCommandException>(() => RouteCompat.Ids(p, "ids"));
        Assert.Equal("invalid_parameter", ex.Code);
        Assert.Contains("ids[1]", ex.Message);
    }

    [Fact]
    public void StrListOrNull_handles_absent_list_and_single_string()
    {
        Assert.Null(RouteCompat.StrListOrNull(new JsonObject(), "nomes_contem"));
        Assert.Null(RouteCompat.StrListOrNull(new JsonObject { ["nomes_contem"] = null }, "nomes_contem"));
        Assert.Equal(new[] { "BWC", "Lavabo" },
            RouteCompat.StrListOrNull(JsonNode.Parse("""{"n":["BWC","Lavabo"]}""")!.AsObject(), "n"));
        Assert.Equal(new[] { "BWC" }, RouteCompat.StrListOrNull(new JsonObject { ["n"] = "BWC" }, "n"));
    }

    [Fact]
    public void Items_requires_objects_and_Req_requires_keys()
    {
        var p = JsonNode.Parse("""{"items":[{"id":1,"x":0.1,"y":0.2}, 5]}""")!.AsObject();
        Assert.Equal("invalid_parameter",
            Assert.Throws<RevitCommandException>(() => RouteCompat.Items(p, "items")).Code);
        var it = new JsonObject { ["id"] = 1 };
        Assert.Equal("bad_request",
            Assert.Throws<RevitCommandException>(() => RouteCompat.Req(it, "x", "items[0]")).Code);
    }

    [Fact]
    public void Echo_keeps_the_callers_value_and_type()
    {
        Assert.Equal("\"123\"", RouteCompat.Echo(JsonValue.Create("123"))!.ToJsonString());
        Assert.Equal("123", RouteCompat.Echo(JsonValue.Create(123L))!.ToJsonString());
        Assert.Null(RouteCompat.Echo(null));
    }

    // ── naming / geometry rules ───────────────────────────────────────────

    [Fact]
    public void RenameCandidates_match_the_route_loop()
    {
        var c = RouteCompat.RenameCandidates("PLANTA").ToList();
        Assert.Equal(20, c.Count);
        Assert.Equal("PLANTA", c[0]);
        Assert.Equal("PLANTA (2)", c[1]);
        Assert.Equal("PLANTA (20)", c[^1]);
    }

    [Fact]
    public void NameMatchesAny_is_case_insensitive_contains()
    {
        Assert.True(RouteCompat.NameMatchesAny("BWC SUÍTE", null));
        Assert.True(RouteCompat.NameMatchesAny("BWC SUÍTE", new[] { "suíte" }));
        Assert.True(RouteCompat.NameMatchesAny("Lavabo", new[] { "bwc", "LAV" }));
        Assert.False(RouteCompat.NameMatchesAny("Sala", new[] { "bwc" }));
        Assert.False(RouteCompat.NameMatchesAny(null, new[] { "bwc" }));
    }

    [Fact]
    public void WallNormalDeg_matches_atan2_of_minus_tx_ty()
    {
        Assert.Equal(-90.0, RouteCompat.WallNormalDeg(1, 0));   // wall along +X
        Assert.Equal(0.0, RouteCompat.WallNormalDeg(0, 1));     // wall along +Y
        Assert.Equal(90.0, RouteCompat.WallNormalDeg(-1, 0));
        Assert.Equal(-45.0, RouteCompat.WallNormalDeg(1, 1));
    }

    [Fact]
    public void DimDir_threshold_is_0_7()
    {
        Assert.Equal("u", RouteCompat.DimDir(1.0));
        Assert.Equal("u", RouteCompat.DimDir(-0.71));
        Assert.Equal("v", RouteCompat.DimDir(0.7));
        Assert.Equal("v", RouteCompat.DimDir(0.0));
    }

    [Fact]
    public void PySort_is_ordinal_with_null_first()
    {
        var names = new[] { "b", null, "B", "á", "a" }.OrderBy(x => x, RouteCompat.PySort).ToList();
        Assert.Equal(new[] { null, "B", "a", "b", "á" }, names);
    }

    [Fact]
    public void Trunc_is_python_slice()
    {
        Assert.Equal("abc", RouteCompat.Trunc("abcdef", 3));
        Assert.Equal("ab", RouteCompat.Trunc("ab", 80));
        Assert.Equal("", RouteCompat.Trunc(null, 5));
    }

    [Fact]
    public void IdKey_is_plain_integer_text()
    {
        Assert.Equal("123456789012", RouteCompat.IdKey(123456789012));
    }
}
