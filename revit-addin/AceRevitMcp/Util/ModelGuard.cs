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
    ///  - deletes warnings (and records them) so transactions commit without the warning dialog; the records (Posted) feed
    ///    the "no new warnings" rule (WarningGate), so deleting a warning never hides a new one from an apply,
    ///  - rolls back transactions that hit real errors (and records why),
    ///  - closes task dialogs with Cancel and acknowledges message boxes that would otherwise block Revit (and records them).
    /// Everything recorded is returned to Claude so it can react.
    /// </summary>
    internal sealed class ModelGuard : IDisposable
    {
        private readonly UIApplication _uiapp;

        public List<string> Warnings { get; } = new List<string>();
        /// <summary>The same warnings as Warnings: the description alone and the failing and additional element ids.</summary>
        public List<(string Text, long[] Ids)> WarningDetails { get; } = new List<(string Text, long[] Ids)>();
        /// <summary>The warnings Revit posted, with their failure definition GUID and failing / additional ids apart (WarningGate).</summary>
        public List<WarningItem> Posted { get; } = new List<WarningItem>();
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
                    var failing = new List<long>();
                    foreach (var id in ids) failing.Add(id.Value);
                    var additional = new List<long>();
                    try { foreach (var id in failure.GetAdditionalElementIds()) additional.Add(id.Value); } catch { /* none */ }
                    string guid = null;
                    try { guid = failure.GetFailureDefinitionId()?.Guid.ToString(); } catch { /* unknown definition */ }
                    var item = new WarningItem(guid, failure.GetDescriptionText(), failing, additional);
                    Posted.Add(item);
                    WarningDetails.Add((item.Text, item.Ids));
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

        /// <summary>The model's warnings now (Document.GetWarnings), keyed like Posted.</summary>
        public static List<WarningItem> ModelWarnings(Document doc)
        {
            var list = new List<WarningItem>();
            if (doc == null) return list;
            IList<FailureMessage> all;
            try { all = doc.GetWarnings(); } catch { return list; }
            foreach (var w in all)
            {
                var failing = new List<long>(); var additional = new List<long>();
                string guid = null;
                try { foreach (var id in w.GetFailingElements()) failing.Add(id.Value); } catch { /* none */ }
                try { foreach (var id in w.GetAdditionalElements()) additional.Add(id.Value); } catch { /* none */ }
                try { guid = w.GetFailureDefinitionId()?.Guid.ToString(); } catch { /* unknown definition */ }
                list.Add(new WarningItem(guid, w.GetDescriptionText(), failing, additional));
            }
            return list;
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            string text = e.DialogId;
            if (e is TaskDialogShowingEventArgs td) text = $"{td.DialogId}: {td.Message}";
            else if (e is MessageBoxShowingEventArgs mb) text = $"{mb.DialogId}: {mb.Message}";

            // Nobody is at the keyboard, and the default button of a dialog can be destructive ("delete these elements?").
            // A task dialog is closed with Cancel (IDCANCEL = 2), which never confirms anything: if the operation needed
            // a yes, the run fails and rolls back, and Claude reports the question to the user. A plain message box
            // only informs, so it gets OK (IDOK = 1).
            // A task dialog is never answered with OK, even when Cancel is not possible: then it is reported, not confirmed.
            var isTask = e is TaskDialogShowingEventArgs;
            var cancelled = isTask && e.OverrideResult(2);
            var answered = cancelled || (!isTask && e.OverrideResult(1));
            Dialogs.Add(cancelled ? $"closed with Cancel (ask the user if it mattered): {text}" : answered ? $"acknowledged: {text}" : $"could not dismiss: {text}");
        }
    }
}
