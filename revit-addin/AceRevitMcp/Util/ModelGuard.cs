using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// While Claude is driving Revit nobody is at the keyboard to click dialogs. For the duration of
    /// one command this:
    ///  - deletes warnings (and records them) so transactions commit without the warning dialog,
    ///  - rolls back transactions that hit real errors (and records why),
    ///  - auto-answers task dialogs / message boxes that would otherwise block Revit (and records them).
    /// Everything recorded is returned to Claude so it can react.
    /// </summary>
    internal sealed class ModelGuard : IDisposable
    {
        private readonly UIApplication _uiapp;

        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<string> Dialogs { get; } = new List<string>();

        public ModelGuard(UIApplication uiapp)
        {
            _uiapp = uiapp;
            _uiapp.Application.FailuresProcessing += OnFailuresProcessing;
            _uiapp.DialogBoxShowing += OnDialogBoxShowing;
        }

        public void Dispose()
        {
            _uiapp.Application.FailuresProcessing -= OnFailuresProcessing;
            _uiapp.DialogBoxShowing -= OnDialogBoxShowing;
        }

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            var accessor = e.GetFailuresAccessor();
            var hasErrors = false;
            foreach (var failure in accessor.GetFailureMessages())
            {
                var text = failure.GetDescriptionText();
                var ids = failure.GetFailingElementIds();
                if (ids.Count > 0) text += $" (elements: {string.Join(", ", ids)})";

                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    Warnings.Add(text);
                    accessor.DeleteWarning(failure);
                }
                else
                {
                    Errors.Add(text);
                    hasErrors = true;
                }
            }

            if (hasErrors)
            {
                // Never show the error dialog; roll the transaction back and report.
                var options = accessor.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                accessor.SetFailureHandlingOptions(options);
                e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            }
            else
            {
                e.SetProcessingResult(FailureProcessingResult.Continue);
            }
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            string text = e.DialogId;
            if (e is TaskDialogShowingEventArgs td) text = $"{td.DialogId}: {td.Message}";
            else if (e is MessageBoxShowingEventArgs mb) text = $"{mb.DialogId}: {mb.Message}";

            // IDOK = 1. Answering keeps Revit responsive; the dialog text is reported back.
            var answered = e.OverrideResult(1);
            Dialogs.Add(answered ? $"auto-accepted: {text}" : $"could not dismiss: {text}");
        }
    }
}
