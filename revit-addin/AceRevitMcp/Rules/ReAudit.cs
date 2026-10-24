using System;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// The automatic re-audit (DM3): after an applied change (a NativeChange tool, the code runner, set_parameters: every
    /// kept ChangeTracker recording that changed elements) and after the user's undo or redo, the model check runs again
    /// with its compliance publish, so the Companion shows what the change did to the requirements without anyone asking.
    /// It runs when Revit is next idle and no bridge request is running, never inside the apply (the apply's reply is
    /// already sent). It is the model check: read-only, no transaction. Only for a model that has a compliance result (a
    /// vault); several changes before Revit is idle give one re-audit; a model check that took longer than
    /// <see cref="MaxAutoSeconds"/> is not repeated by itself (the band says to press Re-run audit). A failure is shown on
    /// the band, never thrown.
    /// </summary>
    internal static class ReAudit
    {
        public const double MaxAutoSeconds = 30;

        private static UIControlledApplication _ui;
        private static bool _attached;
        private static (string Model, string Reason)? _due;
        private static double _lastSeconds;

        public static void Attach(UIControlledApplication ui) => _ui = ui;

        public static void Detach()
        {
            try { if (_attached && _ui != null) _ui.Idling -= OnIdling; } catch { }
            _attached = false; _ui = null; _due = null;
        }

        /// <summary>Asks for a re-audit of this model when Revit is next idle (reason: apply, undo or redo).</summary>
        public static void Due(string model, string reason)
        {
            try
            {
                if (_ui == null || string.IsNullOrEmpty(model) || StatusStore.For(model)?.Compliance == null) return;
                // An apply already waiting keeps its words ("1 undo step") over an undo / redo seen in the same moment.
                if (_due is { } d && d.Model == model && d.Reason == "apply" && reason != "apply") return;
                _due = (model, reason);
                if (_attached) return;
                _ui.Idling += OnIdling;
                _attached = true;
            }
            catch (Exception ex) { Log.Warn($"Re-audit: {ex.Message}"); }
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_due == null) { Unhook(sender); return; }
            if (Bridge.RequestDispatcher.Busy != null) return;   // a request is running: next idle
            var app = sender as UIApplication;
            var due = _due.Value;
            _due = null;
            Unhook(sender);
            if (app != null) Run(app, due.Model, due.Reason);
        }

        private static void Unhook(object sender)
        {
            try { if (sender is UIApplication a) a.Idling -= OnIdling; else if (_ui != null) _ui.Idling -= OnIdling; } catch { }
            _attached = false;
        }

        private static void Run(UIApplication app, string model, string reason)
        {
            var now = DateTime.Now;
            var before = StatusStore.For(model)?.Compliance;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null || doc.Title != model)
                {
                    StatusStore.PublishReAudit(model, ComplianceDeltaLogic.Failed(reason, "the model is no longer the active one", now));
                    return;
                }
                if (_lastSeconds > MaxAutoSeconds)
                {
                    StatusStore.PublishReAudit(model, ComplianceDeltaLogic.Skipped(reason, _lastSeconds, now));
                    return;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = Dashboard.DashboardCommands.Insights(app, new JsonObject());
                _lastSeconds = sw.Elapsed.TotalSeconds;
                Companion.ActivityHub.RecordCommand("get_model_insights", new JsonObject { ["auto"] = "re-audit after " + reason }, result, null, sw.ElapsedMilliseconds);
                var after = StatusStore.For(model)?.Compliance;
                StatusStore.PublishReAudit(model, after == null || ReferenceEquals(after, before)
                    ? ComplianceDeltaLogic.Failed(reason, "the model check ran but compliance could not be evaluated (see the ACE log)", now)
                    : ComplianceDeltaLogic.BandFor(before, after, reason, now));
                Log.Info($"Re-audit after {reason}: {model} in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Warn($"Re-audit after {reason} failed: {ex}");
                StatusStore.PublishReAudit(model, ComplianceDeltaLogic.Failed(reason, ex.Message, now));
            }
        }
    }
}
