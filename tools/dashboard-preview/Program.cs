// Renders the Insights dashboard with sample data, without Revit (Windows):
//   dotnet run --project tools/dashboard-preview -- <path to AceRevitMcp.dll> <output.html>
// Uses the brand in %APPDATA%\ACE-RevitMCP\branding. Maintainers only; not part of the package.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

class P
{
    static Assembly A;
    static Type T(string n) => A.GetType("AceRevitMcp." + n, true);
    static object New(string n) => Activator.CreateInstance(T(n), true);
    static void Set(object o, string f, object v) => o.GetType().GetField(f)!.SetValue(o, v);
    static object Get(object o, string f) => o.GetType().GetField(f)!.GetValue(o);

    [STAThread]
    static void Main(string[] args)
    {
        A = Assembly.LoadFrom(args[0]);
        T("Util.Branding").GetMethod("Load")!.Invoke(null, null);
        var x = New("Dashboard.Insights");
        Set(x, "Model", "Snowdon Towers Sample Architectural"); Set(x, "User", "khaled.rezk"); Set(x, "Revit", "Autodesk Revit 2025.4");
        Set(x, "AddinVersion", "1.5.0"); Set(x, "PathName", @"C:\Samples\Snowdon Towers Sample Architectural.rvt"); Set(x, "FileBytes", 96L * 1048576);
        Set(x, "Score", 72); Set(x, "Grade", "Fair"); Set(x, "ElapsedMs", 3400L);
        var counts = (IDictionary)Get(x, "Counts");
        foreach (var (k, v) in new[] { ("Warnings", 412), ("Model elements", 38211), ("Walls", 1822), ("Doors", 142), ("Windows", 318), ("Rooms", 164), ("Views", 286), ("Sheets", 54), ("Families", 311) }) counts[k] = v;
        var checks = (IList)Get(x, "Checks");
        void C(string area, string name, int count, double pen, double max, string detail, string hint)
        {
            var c = New("Dashboard.Check"); Set(c, "Area", area); Set(c, "Name", name); Set(c, "Count", count); Set(c, "Penalty", pen); Set(c, "MaxPenalty", max);
            Set(c, "Detail", detail); Set(c, "Hint", hint); ((List<long>)Get(c, "Ids")).AddRange(new long[] { 631418, 631497, 631587, 632001, 632077 }); checks.Add(c);
        }
        C("Warnings", "Revit warnings", 412, 20, 20, "412 warnings of 23 types", "Resolve the most frequent types first.");
        C("Views and sheets", "Views not on sheets", 121, 1.4, 8, "121 of 286 views (42%) are not placed on a sheet", "Delete working views that are no longer needed, or place them.");
        C("Rooms and doors", "Rooms without a door", 9, 2.25, 6, "9 enclosed rooms with no door opening into them", "Check that access is intended.");
        C("Rooms and doors", "Rooms not enclosed", 0, 0, 8, "0 placed rooms with no enclosed area", "");
        C("Parameters", "Key parameters filled", 210, 2.8, 8, "65% of key door, window and room parameters are filled", "Fill the missing values.");
        C("Submission readiness", "Sheets with missing title block data", 54, 5, 5, "54 of 54 sheets miss issue date, drawn, checked, approved or designed by", "Fill the title block fields before issue.");
        C("Model content", "Imported CAD (not linked)", 0, 0, 10, "0 CAD files imported into the model", "");
        var wt = Get(x, "WarningTypes"); var add = wt.GetType().GetMethod("Add")!;
        foreach (var (t, n) in new[] { ("Highlighted walls overlap. One of them may be ignored when Revit finds room boundaries.", 188), ("Elements have duplicate 'Mark' values.", 96), ("Room separation line is slightly off axis and may cause inaccuracies.", 61), ("Highlighted elements are joined but do not intersect.", 40) })
            add.Invoke(wt, new object[] { ValueTuple.Create(t, n) });
        var comp = (IList)Get(x, "Completeness");
        foreach (var (cat, par, f, tot) in new[] { ("Doors", "Mark", 142, 142), ("Windows", "Mark", 300, 318), ("Rooms", "Number", 164, 164), ("Rooms", "Name", 164, 164), ("Rooms", "Department", 12, 164), ("Rooms", "Floor finish", 0, 164) })
        { var c = New("Dashboard.Completeness"); Set(c, "Category", cat); Set(c, "Parameter", par); Set(c, "Filled", f); Set(c, "Total", tot); comp.Add(c); }
        var levels = (IList)Get(x, "Levels");
        foreach (var (n, e, r, d, w, wl, a) in new[] { ("Level 1", 0.0, 42, 48, 60, 510, 2410.5), ("Level 2", 4200.0, 40, 36, 86, 440, 2295.0), ("Level 3", 8400.0, 41, 30, 86, 430, 2301.2), ("Roof", 12600.0, 0, 2, 0, 60, 0.0) })
        { var l = New("Dashboard.LevelStats"); Set(l, "Level", n); Set(l, "Elevation", e); Set(l, "Rooms", r); Set(l, "Doors", d); Set(l, "Windows", w); Set(l, "Walls", wl); Set(l, "RoomAreaM2", a); levels.Add(l); }
        var pi = Get(x, "ProjectInfo"); var pa = pi.GetType().GetMethod("Add")!;
        foreach (var (f, ok, v) in new[] { ("Project name", true, "Snowdon Towers"), ("Project number", true, "001-00"), ("Client name", false, ""), ("Project address", false, ""), ("Project status", true, "Design development"), ("Project issue date", false, "") })
            pa.Invoke(pi, new object[] { ValueTuple.Create(f, ok, v) });
        var sf = Get(x, "SheetFields"); var sa = sf.GetType().GetMethod("Add")!;
        foreach (var (f, m) in new[] { ("Sheet issue date", 0), ("Drawn by", 54), ("Checked by", 54), ("Approved by", 54), ("Designed by", 54) }) sa.Invoke(sf, new object[] { ValueTuple.Create(f, m) });
        var act = Get(x, "Activity"); Set(act, "Previews", 14); Set(act, "Changes", 6); Set(act, "PanelApplied", 2); Set(act, "PanelCancelled", 1);
        var pd = Get(act, "PerDay"); var pdl = (IList)pd; var today = DateTime.Today; int[] vals = { 0, 3, 5, 0, 2, 8, 4 };
        for (int i = 0; i < 7; i++) pdl.Add(ValueTuple.Create(today.AddDays(i - 6), vals[i]));
        var latest = (List<string>)Get(act, "Latest"); latest.AddRange(new[] { "Mon 16:40  Change (applied in ACE panel): Companion test", "Mon 16:38  Preview: set parameters", "Sun 11:02  Backup" });
        var hist = new List<(DateTime, int)> { (today.AddDays(-20), 58), (today.AddDays(-14), 63), (today.AddDays(-7), 66), (today.AddDays(-2), 70), (DateTime.Now, 72) };
        Set(x, "History", hist);
        var html = (string)T("Dashboard.DashboardHtml").GetMethod("Render", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new[] { x })!;
        System.IO.File.WriteAllText(args[1], html);
        Console.WriteLine("wrote " + args[1] + " " + html.Length);
    }
}
