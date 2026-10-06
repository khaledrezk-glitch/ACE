using System;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Dashboard
{
    /// <summary>
    /// Bridge "presentation_standard": check or apply the office presentation standard (text height by view scale, one
    /// type per size and kind). { check_only, dry_run, views: [names], text_sizes: [...], unify_tags }. Previewed and
    /// applied like every change (NativeChange): an Apply card in the Companion, ONE undo step.
    /// </summary>
    internal static class PresentationCommands
    {
        public const string Name = "ACE: presentation standard";

        public static JsonNode Run(UIApplication app, JsonObject args) => RunWith(app, args, PresentationStandard.Load(args["text_sizes"] as JsonArray));

        /// <summary>With a given standard (the window passes what is on screen; nothing is saved until the user saves).</summary>
        internal static JsonObject RunWith(UIApplication app, JsonObject args, PresentationStandard std)
        {
            if (args["unify_tags"] is JsonValue ut && ut.TryGetValue<bool>(out var unify)) std.UnifyTagTypes = unify;
            var names = Args.Strings(args, "views").ToHashSet(StringComparer.OrdinalIgnoreCase);
            return NativeChange.Run(app, args, "presentation_standard", Name,
                (doc, plan) => PresentationFix.Run(doc, std, new PresentationFix.Options { PlanOnly = plan, ViewNames = names }), std.Identity());
        }
    }
}
