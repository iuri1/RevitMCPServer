using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Revit side of the "tx_seguro" mode: installs a STRICT <see cref="IFailuresPreprocessor"/> on a
/// transaction. Decisions come from <see cref="TxSeguroState"/>/<see cref="TxSeguroPass"/> (pure,
/// unit-tested); this class only reads the FailureMessageAccessor and applies the action.
///
/// Why: Revit's default handling tries ResolveFailure and, when the error comes back, opens the
/// modal "Error - cannot be ignored" dialog on the UI thread, which freezes the add-in (and the
/// pyRevit routes) until someone clicks. With this preprocessor an error rolls the transaction back
/// silently and its text is returned to the caller in 'falhas'.
/// </summary>
public static class TxSeguro
{
    public static void Install(Transaction tx, TxSeguroState state)
    {
        var fho = tx.GetFailureHandlingOptions();
        fho.SetFailuresPreprocessor(new StrictPreprocessor(state));
        fho.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(fho);
    }

    private sealed class StrictPreprocessor : IFailuresPreprocessor
    {
        private readonly TxSeguroState _state;

        public StrictPreprocessor(TxSeguroState state)
        {
            _state = state;
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var pass = _state.BeginPass();
            try
            {
                foreach (var m in accessor.GetFailureMessages().ToList())
                {
                    string text;
                    List<long> ids;
                    try
                    {
                        text = m.GetDescriptionText();
                        ids = m.GetFailingElementIds().Select(i => i.Value).ToList();
                    }
                    catch
                    {
                        text = "?";
                        ids = new List<long>();
                    }

                    var action = pass.Handle(
                        m.GetSeverity() == FailureSeverity.Warning, m.HasResolutions(), text, ids);
                    switch (action)
                    {
                        case TxSeguroAction.DeleteWarning:
                            accessor.DeleteWarning(m);
                            break;
                        case TxSeguroAction.Resolve:
                            accessor.ResolveFailure(m);
                            break;
                        case TxSeguroAction.Rollback:
                            break;
                    }
                }
            }
            catch (Exception)
            {
                pass.Abort();
            }

            return pass.End() switch
            {
                TxSeguroPassResult.ProceedWithCommit => FailureProcessingResult.ProceedWithCommit,
                TxSeguroPassResult.ProceedWithRollBack => FailureProcessingResult.ProceedWithRollBack,
                _ => FailureProcessingResult.Continue,
            };
        }
    }
}
