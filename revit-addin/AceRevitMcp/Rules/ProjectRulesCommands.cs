using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Bridge "project_rules": { section?, include_rules? }. Read-only: which project requirements file this model uses
    /// (and how it was found), where each section's rules come from, their hash, and optionally the merged rules. The MCP
    /// server reads the workset rules here, so Claude and the model check always use the same rules.
    /// </summary>
    internal static class ProjectRulesCommands
    {
        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = app.ActiveUIDocument?.Document ?? throw new Bridge.CommandException("No model is open.");
            ProjectRules rules;
            try { rules = ProjectRules.For(doc); }
            catch (System.IO.InvalidDataException ex) { throw new Bridge.CommandException(ex.Message); }
            var section = Args.Str(args, "section");
            if (!string.IsNullOrWhiteSpace(section))
            {
                if (!ProjectRules.Sections.Contains(section)) throw new Bridge.CommandException($"Unknown section '{section}'. Known: {string.Join(", ", ProjectRules.Sections)}");
                return new JsonObject
                {
                    ["model"] = doc.Title, ["projectFile"] = rules.ProjectFile, ["foundBy"] = rules.FoundBy, ["cloud"] = doc.IsModelInCloud,
                    ["modelFolders"] = new JsonArray(ProjectRules.ModelFolders(doc).Select(k => (JsonNode)k).ToArray()),
                    ["section"] = section, ["source"] = rules.SourceOf(section), ["hash"] = rules.HashOf(section), ["rules"] = rules.Section(section),
                };
            }
            var o = rules.Describe(Args.Bool(args, "include_rules"));
            o["model"] = doc.Title;
            o["keys"] = new JsonArray(ProjectRules.Keys(doc).Select(k => (JsonNode)k).ToArray());
            o["modelFolders"] = new JsonArray(ProjectRules.ModelFolders(doc).Select(k => (JsonNode)k).ToArray());
            o["cloud"] = doc.IsModelInCloud;
            return o;
        }
    }
}
