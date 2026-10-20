using System;
using System.Collections.Generic;
using System.Linq;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// The plain rules of workset_admin (no Revit types, tested in tools/clash-test): which renames and deletions a request
    /// gives, what blocks them, the plan's fingerprint and the per-category self-check.
    /// </summary>
    internal static class WorksetAdminLogic
    {
        /// <summary>The worksharing default workset for levels and grids: never renamed or deleted.</summary>
        public const string SharedLevelsAndGrids = "Shared Levels and Grids";

        /// <summary>Characters Revit refuses in a workset name (WorksetTable.RenameWorkset).</summary>
        private static readonly char[] Prohibited = { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        internal sealed class Workset
        {
            public long Id;
            public string Name;
            public string Owner;
            public bool Open = true, Editable, IsDefault;
            public bool Protected;     // the datum workset (levels and grids): never renamed or deleted
            public int LevelsAndGrids; // levels and grids on it
            public int Elements;
            public Dictionary<string, int> ByCategory = new Dictionary<string, int>(StringComparer.Ordinal);
            public List<long> SampleIds = new List<long>();
        }

        internal sealed class Step
        {
            public string Id, Kind, Workset, From, To, MoveTo;
            public long WorksetId, MoveToId;
            /// <summary>A delete: the workset that receives anything the count missed (DeleteWorkset never deletes elements). Not in the plan hash.</summary>
            public string Safety;
            public long SafetyId;
            public int Elements;
            public Dictionary<string, int> ByCategory;
        }

        internal sealed class Block
        {
            public string What, Code, Holder, Detail;
            public int Elements;
            public List<long> SampleIds = new List<long>();
        }

        public static bool IsProtected(string name) => string.Equals((name ?? "").Trim(), SharedLevelsAndGrids, StringComparison.OrdinalIgnoreCase);

        /// <summary>A workset that is never renamed or deleted: the datum workset (DatumWorkset), or one named Shared Levels and Grids.</summary>
        public static bool IsProtected(Workset w) => w != null && (w.Protected || IsProtected(w.Name));

        /// <summary>
        /// The datum workset (levels and grids; in real models e.g. "Shared Views, Levels, Grids"), found without relying on its
        /// name: the vault's worksets.datumWorkset when it names a workset; else the workset holding the most levels and grids
        /// (ties: the oldest); else the oldest user workset whose name contains "levels" or "grids". Null when none.
        /// </summary>
        public static Workset DatumWorkset(IList<Workset> worksets, string vaultName)
        {
            if (worksets == null || worksets.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(vaultName) && Find(worksets, vaultName.Trim()) is Workset v) return v;
            var held = worksets.Where(w => w.LevelsAndGrids > 0).OrderByDescending(w => w.LevelsAndGrids).ThenBy(w => w.Id).FirstOrDefault();
            if (held != null) return held;
            return worksets.Where(w => (w.Name ?? "").IndexOf("levels", StringComparison.OrdinalIgnoreCase) >= 0 || (w.Name ?? "").IndexOf("grids", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(w => w.Id).FirstOrDefault();
        }

        /// <summary>Marks the datum workset protected (see DatumWorkset); returns it.</summary>
        public static Workset MarkDatum(IList<Workset> worksets, string vaultName)
        {
            var d = DatumWorkset(worksets, vaultName);
            if (d != null) d.Protected = true;
            return d;
        }

        private static bool OwnedByOther(Workset w, string me) =>
            !string.IsNullOrWhiteSpace(w.Owner) && !string.Equals(w.Owner.Trim(), (me ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static Workset Find(IList<Workset> worksets, string name) =>
            worksets.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal))
            ?? worksets.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Renames as asked: one step per rename that can run, one block per rename that cannot (a no-op is neither).</summary>
        public static (List<Step> Steps, List<Block> Blocks) PlanRename(IList<Workset> worksets, IList<(string From, string To)> renames, string me)
        {
            var steps = new List<Step>(); var blocks = new List<Block>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new HashSet<long>();
            foreach (var (fromRaw, toRaw) in renames ?? new List<(string, string)>())
            {
                var from = (fromRaw ?? "").Trim(); var to = (toRaw ?? "").Trim();
                var what = $"rename {from} > {to}";
                if (from.Length == 0 || to.Length == 0) { blocks.Add(new Block { What = what, Code = "naming", Detail = "a rename needs both from and to" }); continue; }
                var w = Find(worksets, from);
                if (w == null) { blocks.Add(new Block { What = what, Code = "not-found", Detail = $"no user workset is called {from}" }); continue; }
                if (IsProtected(w)) { blocks.Add(new Block { What = what, Code = "system", Detail = $"{w.Name} is the levels and grids workset: never renamed" }); continue; }
                if (string.Equals(w.Name, to, StringComparison.Ordinal)) continue;   // already named so
                if (to.IndexOfAny(Prohibited) >= 0) { blocks.Add(new Block { What = what, Code = "naming", Detail = "a workset name cannot contain { } [ ] | ; < > ? ` ~" }); continue; }
                if (IsProtected(to) || worksets.Any(o => o.Id != w.Id && string.Equals(o.Name, to, StringComparison.OrdinalIgnoreCase)))
                { blocks.Add(new Block { What = what, Code = "exists", Detail = $"a workset called {to} already exists" }); continue; }
                if (!targets.Add(to)) { blocks.Add(new Block { What = what, Code = "exists", Detail = $"two renames in the request give the name {to}" }); continue; }
                if (!sources.Add(w.Id)) { blocks.Add(new Block { What = what, Code = "naming", Detail = $"{w.Name} is renamed twice in the request" }); continue; }
                if (OwnedByOther(w, me)) { blocks.Add(new Block { What = what, Code = "borrowed", Holder = w.Owner.Trim() }); continue; }
                steps.Add(new Step { Id = "rename:" + w.Id, Kind = "rename", Workset = w.Name, From = w.Name, To = to, WorksetId = w.Id, Elements = w.Elements });
            }
            return (steps, blocks);
        }

        /// <summary>
        /// Deletions: without names, every open, empty user workset (closed ones are skipped: their elements cannot be counted);
        /// with names, those worksets. A workset with elements is deleted only when named and move_to is given (its elements
        /// move there first); otherwise it is blocked as not-empty. Nothing is ever deleted with its elements.
        /// </summary>
        public static (List<Step> Steps, List<Block> Blocks, List<string> Skipped) PlanDelete(IList<Workset> worksets, IList<string> names, string moveTo, string me)
        {
            var steps = new List<Step>(); var blocks = new List<Block>(); var skipped = new List<string>();
            var named = names != null && names.Any(n => !string.IsNullOrWhiteSpace(n));
            var candidates = new List<Workset>();
            if (named)
                foreach (var n in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var w = Find(worksets, n);
                    if (w == null) blocks.Add(new Block { What = "delete " + n, Code = "not-found", Detail = $"no user workset is called {n}" });
                    else if (!candidates.Contains(w)) candidates.Add(w);
                }
            else
                foreach (var w in worksets.Where(w => w.Elements == 0 && !IsProtected(w)))
                    if (!w.Open) skipped.Add($"{w.Name} (closed)");
                    else if (OwnedByOther(w, me)) skipped.Add($"{w.Name} (borrowed by {w.Owner.Trim()})");
                    else candidates.Add(w);

            Workset target = null;
            if (!string.IsNullOrWhiteSpace(moveTo))
            {
                target = Find(worksets, moveTo.Trim());
                if (target == null) blocks.Add(new Block { What = "move_to " + moveTo.Trim(), Code = "not-found", Detail = $"no user workset is called {moveTo.Trim()}" });
                else if (candidates.Contains(target)) { blocks.Add(new Block { What = "move_to " + target.Name, Code = "naming", Detail = "move_to is one of the worksets to delete" }); target = null; }
            }

            foreach (var w in candidates)
            {
                var what = "delete " + w.Name;
                if (IsProtected(w)) { blocks.Add(new Block { What = what, Code = "system", Detail = $"{w.Name} is the levels and grids workset: never deleted, even when empty" }); continue; }
                if (!w.Open) { blocks.Add(new Block { What = what, Code = "closed", Detail = $"{w.Name} is closed, so its elements cannot be counted" }); continue; }
                if (OwnedByOther(w, me)) { blocks.Add(new Block { What = what, Code = "borrowed", Holder = w.Owner.Trim(), Elements = w.Elements, SampleIds = w.SampleIds.ToList() }); continue; }
                if (w.Elements > 0 && target == null)
                {
                    blocks.Add(new Block
                    {
                        What = what, Code = "not-empty", Elements = w.Elements, SampleIds = w.SampleIds.ToList(),
                        Detail = $"{w.Name} holds {w.Elements} element{(w.Elements == 1 ? "" : "s")} ({Describe(w.ByCategory)})",
                    });
                    continue;
                }
                if (w.Elements > 0 && OwnedByOther(target, me)) { blocks.Add(new Block { What = "move_to " + target.Name, Code = "borrowed", Holder = target.Owner.Trim() }); continue; }
                steps.Add(w.Elements == 0
                    ? new Step { Id = "delete:" + w.Id, Kind = "delete", Workset = w.Name, WorksetId = w.Id, Elements = 0, ByCategory = new Dictionary<string, int>() }
                    : new Step { Id = "move-and-delete:" + w.Id, Kind = "move-and-delete", Workset = w.Name, WorksetId = w.Id, Elements = w.Elements, ByCategory = w.ByCategory, MoveTo = target.Name, MoveToId = target.Id });
            }
            // Each delete names a workset that receives anything the count missed, so nothing is ever deleted with a workset.
            var gone = new HashSet<long>(steps.Select(x => x.WorksetId));
            foreach (var st in steps.Where(x => x.Kind == "delete"))
            {
                var safe = SafetyTarget(worksets, gone);
                if (safe == null) { blocks.Add(new Block { What = "delete " + st.Workset, Code = "not-found", Detail = "no open workset is left to receive anything the count missed" }); continue; }
                st.Safety = safe.Name; st.SafetyId = safe.Id;
            }
            steps.RemoveAll(x => x.Kind == "delete" && x.Safety == null);
            return (steps, blocks, skipped);
        }

        /// <summary>The workset that receives what an "empty" workset still held: open, not deleted; editable and the default first, protected last.</summary>
        public static Workset SafetyTarget(IList<Workset> worksets, ICollection<long> deleted) =>
            worksets.Where(w => w.Open && !deleted.Contains(w.Id))
                .OrderBy(w => IsProtected(w)).ThenByDescending(w => w.Editable).ThenByDescending(w => w.IsDefault).ThenBy(w => w.Id)
                .FirstOrDefault();

        public static string Describe(Dictionary<string, int> byCategory) =>
            byCategory == null || byCategory.Count == 0 ? "nothing" : string.Join(", ", byCategory.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(6).Select(kv => $"{kv.Key} {kv.Value}"));

        /// <summary>The plan's fingerprint: the same steps on the same worksets with the same element counts.</summary>
        public static string PlanHash(IEnumerable<Step> steps)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var lines = (steps ?? Enumerable.Empty<Step>())
                .Select(s => string.Join("|", s.Kind, s.WorksetId.ToString(inv), s.From ?? "", s.To ?? "", s.MoveToId.ToString(inv), s.Elements.ToString(inv)))
                .OrderBy(l => l, StringComparer.Ordinal);
            return Util.Compat.Sha256Hex(string.Join("\n", lines)).ToLowerInvariant();
        }

        /// <summary>The self-check: every category keeps its element count (only worksets change).</summary>
        public static List<(string What, string Before, string After)> CompareCounts(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var list = new List<(string, string, string)>();
            foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                before.TryGetValue(key, out var b); after.TryGetValue(key, out var a);
                if (a != b) list.Add(("elements: " + key, b.ToString(), a.ToString()));
            }
            return list;
        }

        public static (string Why, string How) Explain(string code, string holder, string detail)
        {
            switch (code)
            {
                case "system": return (detail ?? "a protected workset", "Leave it as it is: the levels and grids workset and the system worksets are never renamed or deleted.");
                case "not-empty": return (detail ?? "the workset holds elements", "Move its elements first: run again with move_to: <workset> and the workset named in worksets, or move them in Revit.");
                case "exists": return (detail ?? "the name is taken", "Choose another name, or delete or rename the other workset first.");
                case "not-found": return (detail ?? "not found", "Check the name against the audit (action: audit).");
                case "not-workshared": return (detail ?? "the model is not workshared", "Worksets exist only in a workshared model (Collaborate > Worksets).");
                case "naming": return (detail ?? "the name is not valid", "Fix the request.");
                case "borrowed":
                    var who = string.IsNullOrWhiteSpace(holder) ? "another user" : holder.Trim();
                    return ($"borrowed by {who}" + (string.IsNullOrEmpty(detail) ? "" : ": " + detail), $"Ask {who} to synchronize with central and relinquish the workset and its elements, then run again.");
                case "closed": return (detail ?? "the workset is closed", "Open the workset (Collaborate > Worksets), then run again.");
                default: return Coordination.ClashResolveLogic.Explain(code, holder, detail);
            }
        }
    }
}
