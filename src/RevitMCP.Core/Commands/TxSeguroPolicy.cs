using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace RevitMCPAddin.Commands;

/// <summary>What the strict preprocessor does with one failure message.</summary>
public enum TxSeguroAction
{
    /// <summary>Warning: dismissed (FailuresAccessor.DeleteWarning).</summary>
    DeleteWarning,
    /// <summary>Error whose text matches <c>resolver_contem</c>: FailuresAccessor.ResolveFailure.</summary>
    Resolve,
    /// <summary>Any other error: the whole transaction rolls back, without a dialog.</summary>
    Rollback,
}

/// <summary>Mirror of Revit's FailureProcessingResult, kept Revit-free so the policy is testable.</summary>
public enum TxSeguroPassResult
{
    Continue,
    ProceedWithCommit,
    ProceedWithRollBack,
}

/// <summary>
/// Per-call options of the "tx_seguro" transaction mode (port of the JRTools route <c>tx_seguro</c>).
///
/// Passed inside the command params under the reserved key <c>tx_seguro</c>:
/// <list type="bullet">
///   <item><c>true</c> — strict mode with defaults;</item>
///   <item><c>{ "nome": "...", "resolver_contem": ["texto", ...] }</c> — transaction name and the
///         error texts that may be resolved (case-insensitive "contains", at most 3 passes);</item>
///   <item><c>false</c>/<c>null</c>/absent — normal dispatcher behaviour, unchanged.</item>
/// </list>
/// </summary>
public sealed class TxSeguroOptions
{
    public const string Key = "tx_seguro";

    public string? Nome { get; }
    public IReadOnlyList<string> ResolverContem { get; }

    public TxSeguroOptions(string? nome, IEnumerable<string>? resolverContem)
    {
        Nome = string.IsNullOrWhiteSpace(nome) ? null : nome;
        ResolverContem = (resolverContem ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s.ToLowerInvariant())
            .ToList();
    }

    /// <summary>
    /// Splits the reserved <c>tx_seguro</c> key out of <paramref name="parameters"/>.
    /// Returns the options (null when the mode is off) and, in <paramref name="commandParams"/>,
    /// the params the command should see (the original object when the key is absent, otherwise a
    /// copy without it). Throws <see cref="RevitCommandException"/> (invalid_parameter) on a
    /// malformed value.
    /// </summary>
    public static TxSeguroOptions? Extract(JsonObject? parameters, out JsonObject commandParams)
    {
        parameters ??= new JsonObject();
        if (!parameters.ContainsKey(Key))
        {
            commandParams = parameters;
            return null;
        }

        var node = parameters[Key];
        var copy = new JsonObject();
        foreach (var kv in parameters)
            if (kv.Key != Key) copy[kv.Key] = kv.Value?.DeepClone();
        commandParams = copy;

        if (node is null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var on)) return on ? new TxSeguroOptions(null, null) : null;
            throw new RevitCommandException("invalid_parameter",
                "Parameter 'tx_seguro' must be true/false or an object {nome?, resolver_contem?}.");
        }
        if (node is not JsonObject o)
            throw new RevitCommandException("invalid_parameter",
                "Parameter 'tx_seguro' must be true/false or an object {nome?, resolver_contem?}.");

        string? nome = null;
        if (o["nome"] is { } n) nome = P.StrFrom(n, "tx_seguro.nome");

        var resolver = new List<string>();
        switch (o["resolver_contem"])
        {
            case null:
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    resolver.Add(P.StrFrom(arr[i], $"tx_seguro.resolver_contem[{i}]"));
                break;
            case JsonValue single when single.TryGetValue<string>(out var s):
                resolver.Add(s);
                break;
            default:
                throw new RevitCommandException("invalid_parameter",
                    "Parameter 'tx_seguro.resolver_contem' must be an array of strings.");
        }
        return new TxSeguroOptions(nome, resolver);
    }
}

/// <summary>
/// The decision logic of the strict failures preprocessor, with no Revit types (unit-testable).
/// Same rules as the JRTools route <c>tx_seguro</c>:
///   - warnings are dismissed;
///   - an error whose text contains one of <c>resolver_contem</c> (case-insensitive), that has
///     resolutions, while fewer than 3 commit passes happened, is resolved (recorded as
///     "resolvido") and the pass asks to commit;
///   - any other error is recorded in <c>falhas</c> and the pass asks to roll back (no dialog);
///   - rollback always wins over commit inside one pass;
///   - an exception while reading the messages rolls back.
/// One <see cref="TxSeguroState"/> lives for the whole transaction (Revit may call the
/// preprocessor several times after a resolution); one <see cref="TxSeguroPass"/> per call.
/// </summary>
public sealed class TxSeguroState
{
    public const int MaxPasses = 3;

    private readonly List<JsonObject> _falhas = new();

    public TxSeguroState(TxSeguroOptions? options)
    {
        Options = options ?? new TxSeguroOptions(null, null);
    }

    public TxSeguroOptions Options { get; }

    /// <summary>Number of passes that ended in ProceedWithCommit (resolutions applied).</summary>
    public int Passes { get; private set; }

    /// <summary>Raw log, in order: {"erro": txt, "ids": [...]} or {"resolvido": txt, "ids": [...]}.</summary>
    public IReadOnlyList<JsonObject> Falhas => _falhas;

    public TxSeguroPass BeginPass() => new(this);

    internal bool CanResolve(bool hasResolutions, string? text)
    {
        if (Options.ResolverContem.Count == 0 || Passes >= MaxPasses || !hasResolutions) return false;
        var t = (text ?? string.Empty).ToLowerInvariant();
        return Options.ResolverContem.Any(r => t.Contains(r));
    }

    internal void Record(string kind, string? text, IEnumerable<long>? ids)
    {
        var arr = new JsonArray();
        foreach (var id in ids ?? Array.Empty<long>()) arr.Add(id);
        _falhas.Add(new JsonObject { [kind] = text, ["ids"] = arr });
    }

    internal void PassCommitted() => Passes++;

    /// <summary>
    /// The 'falhas' list as the route returns it: the errors (resolved ones removed) followed by
    /// one {"n_resolvidos": n} entry.
    /// </summary>
    public JsonArray FalhasParaResposta()
    {
        var outArr = new JsonArray();
        var resolvidos = 0;
        foreach (var f in _falhas)
        {
            if (f.ContainsKey("resolvido")) { resolvidos++; continue; }
            outArr.Add(f.DeepClone());
        }
        outArr.Add(new JsonObject { ["n_resolvidos"] = resolvidos });
        return outArr;
    }

    /// <summary>
    /// Response payload identical to the route's:
    /// { status, falhas, resultado (when committed), resultado_descartado (otherwise) }.
    /// <paramref name="status"/> is the TransactionStatus name ("Committed", "RolledBack", ...).
    /// </summary>
    public JsonObject BuildResponse(string status, JsonNode? resultado)
    {
        var committed = status == "Committed";
        return new JsonObject
        {
            ["status"] = status,
            ["falhas"] = FalhasParaResposta(),
            ["resultado"] = committed ? resultado?.DeepClone() : null,
            ["resultado_descartado"] = committed ? null : resultado?.DeepClone(),
        };
    }
}

/// <summary>One call of PreprocessFailures.</summary>
public sealed class TxSeguroPass
{
    private readonly TxSeguroState _state;

    internal TxSeguroPass(TxSeguroState state)
    {
        _state = state;
    }

    public TxSeguroPassResult Result { get; private set; } = TxSeguroPassResult.Continue;

    /// <summary>
    /// Classifies one failure message and records it. The caller applies the returned action to
    /// the real FailuresAccessor.
    /// </summary>
    public TxSeguroAction Handle(bool isWarning, bool hasResolutions, string? text, IEnumerable<long>? ids)
    {
        if (isWarning) return TxSeguroAction.DeleteWarning;

        if (_state.CanResolve(hasResolutions, text))
        {
            _state.Record("resolvido", text, ids);
            if (Result != TxSeguroPassResult.ProceedWithRollBack)
                Result = TxSeguroPassResult.ProceedWithCommit;
            return TxSeguroAction.Resolve;
        }

        _state.Record("erro", text, ids);
        Result = TxSeguroPassResult.ProceedWithRollBack;
        return TxSeguroAction.Rollback;
    }

    /// <summary>Anything threw while reading/applying the messages: roll back.</summary>
    public void Abort() => Result = TxSeguroPassResult.ProceedWithRollBack;

    /// <summary>Ends the pass; counts it when it asked to commit (resolutions applied).</summary>
    public TxSeguroPassResult End()
    {
        if (Result == TxSeguroPassResult.ProceedWithCommit) _state.PassCommitted();
        return Result;
    }
}
