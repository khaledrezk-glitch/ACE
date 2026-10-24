using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// The "no new warnings" rule around one apply (G24), shared by the code runner and NativeChange: the model's warnings are
    /// read before the change; after the commit (inside the transaction group, before it is kept or rolled back) the
    /// model's warnings and the ones ModelGuard caught are compared with them (WarningLogic). An apply with a new warning the
    /// call did not accept (accept_warnings: descriptions or GUIDs; accept_new_warnings: true for all) is rolled back whole;
    /// a dry run reports what the apply would add and that it would refuse. fixes_warnings (a warning-fix run): the named
    /// warnings the run touched must be gone, else it fails the same way.
    /// </summary>
    internal sealed class WarningGate
    {
        private readonly List<WarningItem> _before;
        private readonly List<string> _accept;
        private readonly bool _acceptAll;
        private readonly List<string> _fixes;

        public WarningLogic.Outcome Outcome { get; private set; }
        public WarningLogic.Targets Targets { get; private set; }

        private WarningGate(Document doc, JsonObject args)
        {
            _before = ModelGuard.ModelWarnings(doc);
            _accept = Commands.Args.Strings(args, "accept_warnings");
            _acceptAll = Commands.Args.Bool(args, "accept_new_warnings");
            _fixes = Commands.Args.Strings(args, "fixes_warnings");
        }

        /// <summary>Reads the model's warnings before the change (call before the transaction group starts).</summary>
        public static WarningGate Begin(Document doc, JsonObject args) => new WarningGate(doc, args ?? new JsonObject());

        /// <summary>Compares after the commit; true when the change may be kept (or, for a dry run, the identical apply would be).</summary>
        public bool Check(Document doc, ModelGuard guard, ChangeTracker.Recording recording)
        {
            var after = ModelGuard.ModelWarnings(doc);
            Outcome = WarningLogic.Compare(_before, after, guard.Posted, _accept, _acceptAll);
            if (_fixes.Count > 0)
            {
                var touched = new HashSet<long>(recording?.ModifiedIds ?? new long[0]);
                foreach (var id in recording?.DeletedIds ?? new long[0]) touched.Add(id);
                Targets = WarningLogic.Targeted(_before, after, guard.Posted, _fixes, touched);
            }
            return !Refuse;
        }

        public bool Refuse => (Outcome?.Refuse ?? false) || (Targets != null && !Targets.Ok);

        /// <summary>The refusal code: new-warnings, or warnings-remain for a fix run that left its targets.</summary>
        public string Stage => Outcome?.Refuse == true ? "new-warnings" : "warnings-remain";

        public string Error => Outcome?.Refuse == true ? WarningLogic.RefusalError(Outcome)
            : $"Rolled back: {WarningLogic.Line(Targets, false)} Nothing was changed.";

        public string Hint => Outcome?.Refuse == true ? WarningLogic.AcceptHint
            : "Fix the remaining warnings' cause (or limit the run to the warnings it can fix), preview again, then apply the identical call.";

        /// <summary>Adds newWarnings (and targetedWarnings for a fix run) to a result.</summary>
        public void Report(JsonObject result, bool dryRun)
        {
            if (Outcome != null) result["newWarnings"] = WarningLogic.ToJson(Outcome, dryRun);
            if (Targets != null) result["targetedWarnings"] = WarningLogic.ToJson(Targets, dryRun);
        }

        /// <summary>A short line for a result's note.</summary>
        public string Line(bool dryRun) => string.Join(" ", new[] { Outcome != null ? WarningLogic.Line(Outcome, dryRun) : null, Targets != null ? WarningLogic.Line(Targets, dryRun) : null }.Where(x => x != null));
    }
}
