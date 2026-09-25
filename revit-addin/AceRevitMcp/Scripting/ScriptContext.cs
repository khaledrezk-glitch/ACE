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
