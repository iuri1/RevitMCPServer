using System.Linq;
using System.Text.Json.Nodes;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// The strict failures-preprocessor policy (port of the JRTools route tx_seguro), tested without
/// Revit: option parsing, per-message decision, rollback precedence, 3-pass limit and the response
/// shape { status, falhas, resultado, resultado_descartado }.
/// </summary>
public class TxSeguroPolicyTests
{
    private static TxSeguroState State(params string[] resolver) =>
        new(new TxSeguroOptions(null, resolver));

    // ── option parsing ────────────────────────────────────────────────────

    [Fact]
    public void Extract_without_key_returns_null_and_the_same_params()
    {
        var p = new JsonObject { ["a"] = 1 };
        var opts = TxSeguroOptions.Extract(p, out var cmd);
        Assert.Null(opts);
        Assert.Same(p, cmd);
    }

    [Fact]
    public void Extract_true_enables_with_defaults_and_strips_the_key()
    {
        var p = new JsonObject { ["a"] = 1, ["tx_seguro"] = true };
        var opts = TxSeguroOptions.Extract(p, out var cmd);
        Assert.NotNull(opts);
        Assert.Null(opts!.Nome);
        Assert.Empty(opts.ResolverContem);
        Assert.False(cmd.ContainsKey("tx_seguro"));
        Assert.Equal(1, cmd["a"]!.GetValue<int>());
        Assert.True(p.ContainsKey("tx_seguro"));   // the caller's object is not mutated
    }

    [Fact]
    public void Extract_false_or_null_is_off_but_still_strips_the_key()
    {
        foreach (var v in new JsonNode?[] { false, null })
        {
            var p = new JsonObject { ["tx_seguro"] = v };
            Assert.Null(TxSeguroOptions.Extract(p, out var cmd));
            Assert.False(cmd.ContainsKey("tx_seguro"));
        }
    }

    [Fact]
    public void Extract_object_reads_nome_and_lowercases_resolver()
    {
        var p = JsonNode.Parse("""{"tx_seguro":{"nome":"Bis: escala","resolver_contem":["Não É possível", ""]}}""")!.AsObject();
        var opts = TxSeguroOptions.Extract(p, out _)!;
        Assert.Equal("Bis: escala", opts.Nome);
        Assert.Equal(new[] { "não é possível" }, opts.ResolverContem);
    }

    [Theory]
    [InlineData("""{"tx_seguro":"sim"}""")]
    [InlineData("""{"tx_seguro":[1]}""")]
    [InlineData("""{"tx_seguro":{"resolver_contem":{"x":1}}}""")]
    public void Extract_malformed_value_is_invalid_parameter(string json)
    {
        var p = JsonNode.Parse(json)!.AsObject();
        var ex = Assert.Throws<RevitCommandException>(() => TxSeguroOptions.Extract(p, out _));
        Assert.Equal("invalid_parameter", ex.Code);
    }

    // ── decision per message ──────────────────────────────────────────────

    [Fact]
    public void Warning_is_dismissed_and_not_recorded()
    {
        var s = State();
        var pass = s.BeginPass();
        Assert.Equal(TxSeguroAction.DeleteWarning, pass.Handle(true, true, "aviso", new long[] { 1 }));
        Assert.Equal(TxSeguroPassResult.Continue, pass.End());
        Assert.Empty(s.Falhas);
    }

    [Fact]
    public void Error_without_resolver_rolls_back_and_is_recorded()
    {
        var s = State();
        var pass = s.BeginPass();
        Assert.Equal(TxSeguroAction.Rollback, pass.Handle(false, true, "Elementos se sobrepõem", new long[] { 10, 11 }));
        Assert.Equal(TxSeguroPassResult.ProceedWithRollBack, pass.End());
        var f = Assert.Single(s.Falhas);
        Assert.Equal("Elementos se sobrepõem", f["erro"]!.GetValue<string>());
        Assert.Equal(new long[] { 10, 11 }, f["ids"]!.AsArray().Select(n => n!.GetValue<long>()));
    }

    [Fact]
    public void Matching_error_with_resolutions_is_resolved_and_commits()
    {
        var s = State("manter unidos");
        var pass = s.BeginPass();
        Assert.Equal(TxSeguroAction.Resolve,
            pass.Handle(false, true, "Não é possível MANTER UNIDOS os elementos", new long[] { 5 }));
        Assert.Equal(TxSeguroPassResult.ProceedWithCommit, pass.End());
        Assert.Equal(1, s.Passes);
    }

    [Fact]
    public void Matching_error_without_resolutions_rolls_back()
    {
        var s = State("unidos");
        var pass = s.BeginPass();
        Assert.Equal(TxSeguroAction.Rollback, pass.Handle(false, false, "manter unidos", null));
        Assert.Equal(TxSeguroPassResult.ProceedWithRollBack, pass.End());
    }

    [Fact]
    public void Rollback_wins_over_commit_in_either_order()
    {
        var s1 = State("unidos");
        var a = s1.BeginPass();
        a.Handle(false, true, "unidos", null);
        a.Handle(false, true, "outro erro", null);
        Assert.Equal(TxSeguroPassResult.ProceedWithRollBack, a.End());

        var s2 = State("unidos");
        var b = s2.BeginPass();
        b.Handle(false, true, "outro erro", null);
        Assert.Equal(TxSeguroAction.Resolve, b.Handle(false, true, "unidos", null));
        Assert.Equal(TxSeguroPassResult.ProceedWithRollBack, b.End());
        Assert.Equal(0, s2.Passes);
    }

    [Fact]
    public void After_three_committed_passes_the_same_error_rolls_back()
    {
        var s = State("unidos");
        for (var i = 0; i < TxSeguroState.MaxPasses; i++)
        {
            var p = s.BeginPass();
            Assert.Equal(TxSeguroAction.Resolve, p.Handle(false, true, "unidos", null));
            Assert.Equal(TxSeguroPassResult.ProceedWithCommit, p.End());
        }
        var last = s.BeginPass();
        Assert.Equal(TxSeguroAction.Rollback, last.Handle(false, true, "unidos", null));
        Assert.Equal(TxSeguroPassResult.ProceedWithRollBack, last.End());
    }

    [Fact]
    public void Abort_rolls_back_and_does_not_count_a_pass()
    {
        var s = State("x");
        var p = s.BeginPass();
        p.Handle(false, true, "x", null);
        p.Abort();
        Assert.Equal(TxSeguroPassResult.ProceedWithRollBack, p.End());
        Assert.Equal(0, s.Passes);
    }

    // ── response shape ────────────────────────────────────────────────────

    [Fact]
    public void Committed_response_has_resultado_and_counts_resolved()
    {
        var s = State("unidos");
        var p = s.BeginPass();
        p.Handle(false, true, "unidos", new long[] { 1 });
        p.End();

        var r = s.BuildResponse("Committed", new JsonObject { ["id"] = 7 });
        Assert.Equal("Committed", r["status"]!.GetValue<string>());
        Assert.Equal(7, r["resultado"]!["id"]!.GetValue<int>());
        Assert.Null(r["resultado_descartado"]);
        Assert.True(r.ContainsKey("resultado_descartado"));
        var falhas = r["falhas"]!.AsArray();
        var last = Assert.Single(falhas);              // resolved entries are folded into the count
        Assert.Equal(1, last!["n_resolvidos"]!.GetValue<int>());
    }

    [Fact]
    public void RolledBack_response_moves_result_to_resultado_descartado()
    {
        var s = State();
        var p = s.BeginPass();
        p.Handle(false, true, "erro grave", new long[] { 3 });
        p.End();

        var r = s.BuildResponse("RolledBack", new JsonArray { 1, 2 });
        Assert.Null(r["resultado"]);
        Assert.Equal(2, r["resultado_descartado"]!.AsArray().Count);
        var falhas = r["falhas"]!.AsArray();
        Assert.Equal(2, falhas.Count);
        Assert.Equal("erro grave", falhas[0]!["erro"]!.GetValue<string>());
        Assert.Equal(0, falhas[1]!["n_resolvidos"]!.GetValue<int>());
    }

    [Fact]
    public void Response_serializes_like_the_route()
    {
        var s = State();
        var json = s.BuildResponse("Committed", null).ToJsonString();
        Assert.Equal("""{"status":"Committed","falhas":[{"n_resolvidos":0}],"resultado":null,"resultado_descartado":null}""", json);
    }
}
