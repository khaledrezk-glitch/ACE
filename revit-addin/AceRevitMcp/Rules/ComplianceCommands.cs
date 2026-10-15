using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Bridge "compliance": { run?, status?, max_results? }. Read-only. Returns compliance per requirement from the last
    /// model check (the status store), saying when it is out of date; runs the model check first only when there is no
    /// result yet or <c>run</c> is true. <c>status</c> filters the results (e.g. "not-met").
    /// </summary>
    internal static class ComplianceCommands
    {
        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            ProjectRules rules;
            try { rules = ProjectRules.For(doc); }
            catch (System.IO.InvalidDataException ex) { throw new Bridge.CommandException(ex.Message); }
            if (Compliance.VaultOf(rules) == null)
                throw new Bridge.CommandException("This model has no project requirements file, so there is nothing to check compliance against. Create a vault first (vault action init), or map the model to its project folder.");

            var run = StatusStore.For(doc.Title)?.Compliance;
            var ranNow = false;
            if (run == null || Args.Bool(args, "run"))
            {
                Dashboard.DashboardCommands.Insights(app, new JsonObject());
                run = StatusStore.For(doc.Title)?.Compliance;
                ranNow = true;
            }
            if (run == null) throw new Bridge.CommandException("The model check ran but compliance could not be evaluated; see the ACE log.");

            var max = Args.Int(args, "max_results", 60);
            var status = Args.Str(args, "status");
            var filtered = string.IsNullOrWhiteSpace(status) ? run : new Compliance.Run
            {
                Id = run.Id, Model = run.Model, Engine = run.Engine, RulesHash = run.RulesHash, RequirementsHash = run.RequirementsHash,
                User = run.User, Time = run.Time, ProjectFile = run.ProjectFile, Vault = run.Vault, Summary = run.Summary,
                NotMetBySeverity = run.NotMetBySeverity, Results = run.Results.Where(r => string.Equals(r.Status, status, System.StringComparison.OrdinalIgnoreCase)).ToList(),
            };
            var o = Compliance.ToJson(filtered, max);
            o["ranNow"] = ranNow;
            o["outOfDate"] = ranNow ? null : Compliance.OutOfDate(run, rules);
            o["shown"] = System.Math.Min(max, filtered.Results.Count);
            o["of"] = filtered.Results.Count;
            o["recordSaved"] = Compliance.Trusted(run.Vault);
            return o;
        }
    }
}
