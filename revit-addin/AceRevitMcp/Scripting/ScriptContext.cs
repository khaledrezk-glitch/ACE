using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Scripting
{
    /// <summary>
    /// Everything a script can reach. Inside a script these are also exposed as locals:
    /// doc, uidoc, uiapp, app, ctx, args.
    /// </summary>
    public sealed class ScriptContext
    {
        private readonly List<string> _output = new List<string>();

        internal ScriptContext(UIApplication uiapp, JsonObject args, bool dryRun)
        {
            UiApp = uiapp;
            App = uiapp.Application;
            UiDoc = uiapp.ActiveUIDocument;
            Doc = UiDoc?.Document;
            Args = args ?? new JsonObject();
            IsDryRun = dryRun;
        }

        public UIApplication UiApp { get; }
        public Application App { get; }
        public UIDocument UiDoc { get; }
        public Document Doc { get; }

        /// <summary>Inputs passed by the caller ("inputs" in execute_revit_code / run_saved_script).</summary>
        public JsonObject Args { get; }

        /// <summary>True when the changes will be rolled back after the script finishes.</summary>
        public bool IsDryRun { get; }

        internal IReadOnlyList<string> Output => _output;

        /// <summary>True when the user cancelled this run in Claude. Check it in long loops and stop early.</summary>
        public bool Cancelled => Bridge.RequestDispatcher.RunningToken.IsCancellationRequested;

        /// <summary>Stops the run (everything is rolled back) when the user cancelled it in Claude.</summary>
        public void ThrowIfCancelled()
        {
            if (Cancelled) throw new OperationCanceledException("Cancelled by the user. Nothing was changed.");
        }

        /// <summary>Print a line that is returned to Claude along with the result.</summary>
        public void Log(object message)
        {
            if (_output.Count < 2000) _output.Add(message?.ToString() ?? "null");
            else if (_output.Count == 2000) _output.Add("... further output truncated");
        }

        // ---- Input helpers ----------------------------------------------------------------------

        public string Str(string name, string fallback = null) =>
            Args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : Args[name]?.ToString() ?? fallback;

        public double Num(string name, double fallback = 0) =>
            Args[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d
            : double.TryParse(Args[name]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p
            : fallback;

        public bool Bool(string name, bool fallback = false) =>
            Args[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

        public List<long> Ids(string name) =>
            Args[name] is JsonArray a ? a.Select(x => x.GetValue<long>()).ToList() : new List<long>();

        // ---- Unit helpers (Revit stores lengths in decimal feet internally) -----------------------

        public double Mm(double millimetres) => UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);
        public double M(double metres) => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);
        public double Cm(double centimetres) => UnitUtils.ConvertToInternalUnits(centimetres, UnitTypeId.Centimeters);
        public double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        public double ToM(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        public double SqmFromInternal(double sqft) => UnitUtils.ConvertFromInternalUnits(sqft, UnitTypeId.SquareMeters);
        public double Deg(double degrees) => degrees * Math.PI / 180.0;

        // ---- Common lookups ---------------------------------------------------------------------

        public ElementId Id(long value) => new ElementId(value);
        public Element El(long id) => Doc.GetElement(new ElementId(id));

        public FilteredElementCollector Collect() => new FilteredElementCollector(Doc);

        public List<T> All<T>() where T : Element =>
            new FilteredElementCollector(Doc).OfClass(typeof(T)).WhereElementIsNotElementType().Cast<T>().ToList();

        public List<T> Types<T>() where T : ElementType =>
            new FilteredElementCollector(Doc).OfClass(typeof(T)).Cast<T>().ToList();

        public List<Element> Instances(BuiltInCategory category) =>
            new FilteredElementCollector(Doc).OfCategory(category).WhereElementIsNotElementType().ToList();

        public Level Level(string name) =>
            new FilteredElementCollector(Doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

        public List<Level> Levels() =>
            new FilteredElementCollector(Doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();

        public List<Element> Selection() =>
            UiDoc == null ? new List<Element>() : UiDoc.Selection.GetElementIds().Select(Doc.GetElement).Where(e => e != null).ToList();

        // ---- Worksharing ----------------------------------------------------------------------------

        private readonly List<string> _skipped = new List<string>();
        internal IReadOnlyList<string> Skipped => _skipped;

        /// <summary>
        /// Whether the element can be changed now. In a workshared model it cannot when someone else owns it, or when it
        /// was changed in the central model since your last Reload Latest (Revit would refuse the whole transaction).
        /// </summary>
        public bool CanEdit(Element e, out string reason)
        {
            reason = null;
            if (e == null) { reason = "not found"; return false; }
            if (!Doc.IsWorkshared) return true;
            var status = WorksharingUtils.GetCheckoutStatus(Doc, e.Id, out var owner);
            if (status == CheckoutStatus.OwnedByOtherUser) { reason = $"in use by {owner}"; return false; }
            var updates = WorksharingUtils.GetModelUpdatesStatus(Doc, e.Id);
            if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral)
            { reason = "changed in the central model since your last Reload Latest"; return false; }
            return true;
        }

        /// <summary>
        /// The elements that can be changed now; the others are left out and reported to Claude ("skippedUneditable"),
        /// so one element in use by a colleague does not roll back the whole job. Use it before editing a list.
        /// </summary>
        public List<T> Editable<T>(IEnumerable<T> elements) where T : Element
        {
            var ok = new List<T>();
            foreach (var e in elements)
            {
                if (CanEdit(e, out var why)) ok.Add(e);
                else if (_skipped.Count < 500) _skipped.Add($"{e?.Id.Value}: {e?.Name} ({why})");
            }
            return ok;
        }

        /// <summary>
        /// Run a named transaction (for mode "manual"). Warnings are cleared automatically;
        /// returns the final status (Committed / RolledBack).
        /// </summary>
        public TransactionStatus Transact(string name, Action body)
        {
            using var t = new Transaction(Doc, name);
            t.Start();
            try
            {
                body();
                return t.Commit();
            }
            catch
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                throw;
            }
        }
    }
}
