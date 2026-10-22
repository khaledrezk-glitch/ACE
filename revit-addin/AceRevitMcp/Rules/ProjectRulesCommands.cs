using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Bridge "project_rules": { section?, include_rules? }. Read-only: which project requirements file this model uses
    /// (and how it was found), its vault folder (null without a project file), where each section's rules come from, their hash, and optionally the merged rules. The MCP
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
            string vault = null;
            try { vault = Compliance.VaultOf(rules); } catch { vault = null; }   // where the project's documents (and mapping file) live
            var section = Args.Str(args, "section");
            if (!string.IsNullOrWhiteSpace(section))
            {
                if (!ProjectRules.Sections.Contains(section)) throw new Bridge.CommandException($"Unknown section '{section}'. Known: {string.Join(", ", ProjectRules.Sections)}");
                return new JsonObject
                {
                    ["model"] = doc.Title, ["projectFile"] = rules.ProjectFile, ["vault"] = vault, ["foundBy"] = rules.FoundBy, ["cloud"] = doc.IsModelInCloud,
                    ["modelFolders"] = new JsonArray(ProjectRules.ModelFolders(doc).Select(k => (JsonNode)k).ToArray()),
                    ["modelNames"] = new JsonArray(WorksetRules.ModelNames(doc).Select(k => (JsonNode)k).ToArray()),   // a rule's models key (G9)
                    ["section"] = section, ["source"] = rules.SourceOf(section), ["hash"] = rules.HashOf(section), ["rules"] = rules.Section(section),
                };
            }
            var o = rules.Describe(Args.Bool(args, "include_rules"));
            o["model"] = doc.Title;
            o["keys"] = new JsonArray(ProjectRules.Keys(doc).Select(k => (JsonNode)k).ToArray());
            o["modelFolders"] = new JsonArray(ProjectRules.ModelFolders(doc).Select(k => (JsonNode)k).ToArray());
            o["cloud"] = doc.IsModelInCloud;
            o["vault"] = vault;
            o["modelNames"] = new JsonArray(WorksetRules.ModelNames(doc).Select(k => (JsonNode)k).ToArray());   // a rule's models key (G9)
            o["modelKey"] = ModelKey(doc);   // the name the vault records project data under (export.json nwc.agreedViews, #36)
            return o;
        }

        /// <summary>RuleModels.RecordKey for a document: its central's file name, else its file's, else its title.</summary>
        internal static string ModelKey(Autodesk.Revit.DB.Document doc)
        {
            string central = null;
            try { if (doc.IsWorkshared) central = Autodesk.Revit.DB.ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch { central = null; }
            string file = null;
            try { file = doc.PathName; } catch { file = null; }
            return RuleModels.RecordKey(doc.Title, central, file);
        }
    }
}
