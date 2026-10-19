using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// The pure rules of the 1.11.0 parameter plan (no Revit types, tested in tools/checkset-test): derive rules, binding
    /// entries, version match, plain-language block and skip reasons, the self-check comparison and the sheet count of
    /// the impact scan.
    /// </summary>
    internal static class ParameterPlanLogic
    {
        // ---- derive rules ------------------------------------------------------------------------------------

        internal sealed class DeriveRule
        {
            public string Target;
            public string CopyFrom;                               // copy rule: the source parameter
            public List<string> Join;                             // join rule: the parts, in order
            public string Value;                                  // literal rule: this text, into truly blank targets only (never over NA)
            public string Separator = "";
            public string Text;                                   // "A = B" or "A = C + '-' + D", for people
            public JsonObject Source;                             // the rule as sent, echoed
        }

        /// <summary>
        /// The request's derive rules: { target, copyFrom }, { target, join: [...], separator } or { target, value } (a
        /// literal, e.g. a Project Information value). A rule with no target, more or fewer than one of copyFrom, join and
        /// value, an empty join or value, or the target among its own sources comes back in
        /// <paramref name="problems"/> with its reason (never guessed).
        /// </summary>
        public static List<DeriveRule> ParseDerive(JsonArray rules, JsonArray problems)
        {
            var list = new List<DeriveRule>();
            foreach (var node in rules ?? new JsonArray())
            {
                if (node is not JsonObject o) { problems?.Add(new JsonObject { ["rule"] = node?.DeepClone(), ["reason"] = "not a rule object" }); continue; }
                var target = Text(o, "target")?.Trim();
                var copy = Text(o, "copyFrom")?.Trim();
                var join = o["join"] is JsonArray ja
                    ? ja.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s?.Trim() : null).ToList()
                    : null;
                var sep = Text(o, "separator") ?? "";
                var hasValue = o.ContainsKey("value");
                var literal = Text(o, "value");
                var forms = (string.IsNullOrEmpty(copy) ? 0 : 1) + (join == null ? 0 : 1) + (hasValue ? 1 : 0);
                string reason = string.IsNullOrEmpty(target) ? "no target parameter"
                    : forms > 1 ? (hasValue ? "more than one of copyFrom, join and value: give one of them" : "both copyFrom and join: give one of them")
                    : forms == 0 ? "neither copyFrom nor join (nor a value)"
                    : hasValue && string.IsNullOrWhiteSpace(literal) ? "value must be a non-empty text"
                    : join != null && (join.Count == 0 || join.Any(string.IsNullOrEmpty)) ? "join needs a list of parameter names (no blanks)"
                    : string.Equals(copy, target, StringComparison.Ordinal) || (join != null && join.Contains(target)) ? "the target is one of its own sources"
                    : null;
                if (reason != null) { problems?.Add(new JsonObject { ["rule"] = o.DeepClone(), ["target"] = target, ["reason"] = reason }); continue; }
                list.Add(new DeriveRule
                {
                    Target = target, CopyFrom = string.IsNullOrEmpty(copy) ? null : copy, Join = join, Separator = sep, Value = hasValue ? literal : null,
                    Text = hasValue ? $"{target} = '{literal}'"
                        : join == null ? $"{target} = {copy}" : $"{target} = " + string.Join(sep.Length > 0 ? $" + '{sep}' + " : " + ", join),
                    Source = (JsonObject)o.DeepClone(),
                });
            }
            return list;
        }

        /// <summary>
        /// The derived value for one element, or null with the reason: <paramref name="read"/> gives a source's text (null
        /// when the element has no such parameter, "" when blank). Every part must have a value; a part declared missing
        /// (an NA value) is not a value. code: source-missing | source-blank | source-na; part: the source concerned.
        /// </summary>
        public static string DeriveValue(DeriveRule rule, Func<string, string> read, IList<string> naValues, out string code, out string part)
        {
            code = null; part = null;
            if (rule.Value != null) return rule.Value;   // a literal needs no source
            var parts = rule.Join ?? new List<string> { rule.CopyFrom };
            var values = new List<string>();
            foreach (var p in parts)
            {
                var v = read(p);
                if (v == null) { code = "source-missing"; part = p; return null; }
                if (string.IsNullOrWhiteSpace(v)) { code = "source-blank"; part = p; return null; }
                if (ParameterFillLogic.IsNA(v, naValues)) { code = "source-na"; part = p; return null; }
                values.Add(v);
            }
            return string.Join(rule.Separator ?? "", values);
        }

        // ---- binding entries ------------------------------------------------------------------------------------

        internal sealed class BindEntry
        {
            public string Name;
            public Guid Guid;
            public string DataType;      // as in the shared parameter file, e.g. TEXT (null: not checked)
            public string Group;         // project | rooms | levels | elements
            public string Level;         // instance | type
            public List<string> Categories = new List<string>();
            public string File;
            public string GroupTypeId;   // e.g. General (null: General)
            public string FileHash;      // sha256 of the file's bytes at the preview (null: not checked)
        }

        /// <summary>
        /// The request's bind entries. A GUID is required (ACE never creates one) and so is the shared parameter file;
        /// Project Information entries without categories bind to OST_ProjectInformation. Entries that cannot be used come
        /// back in <paramref name="problems"/> as { name, reason }.
        /// </summary>
        public static List<BindEntry> ParseBind(JsonArray entries, JsonArray problems)
        {
            var list = new List<BindEntry>();
            foreach (var node in entries ?? new JsonArray())
            {
                if (node is not JsonObject o) { problems?.Add(new JsonObject { ["name"] = null, ["reason"] = "not a binding object" }); continue; }
                var name = Text(o, "name")?.Trim();
                var guidText = Text(o, "guid")?.Trim();
                var file = Text(o, "file")?.Trim();
                var group = ParameterFillLogic.GroupKey(Text(o, "group") ?? "elements");
                var levelText = Text(o, "level");
                var level = ParameterFillLogic.LevelKey(levelText);
                if (level == "any") level = "instance";
                var cats = new List<string>();
                if (o["categories"] is JsonArray ca)
                    foreach (var n in ca) if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && !cats.Contains(s.Trim())) cats.Add(s.Trim());
                if (group == "project" && cats.Count == 0) cats.Add("OST_ProjectInformation");
                Guid guid = Guid.Empty;
                string reason = string.IsNullOrEmpty(name) ? "no parameter name"
                    : string.IsNullOrEmpty(guidText) || !Guid.TryParse(guidText, out guid) || guid == Guid.Empty ? "no valid GUID: ACE binds only a definition from a shared parameter file, it never creates a GUID"
                    : string.IsNullOrEmpty(file) ? "no shared parameter file: the definition comes only from a file"
                    : group == null ? $"unknown group '{Text(o, "group")}'"
                    : level == null ? $"unknown level '{levelText}'"
                    : cats.Count == 0 ? "no categories to bind to"
                    : list.Any(b => b.Guid == guid) ? "duplicate of an earlier binding (same GUID): merge the categories into one entry"
                    : null;
                if (reason != null) { problems?.Add(new JsonObject { ["name"] = name, ["reason"] = reason }); continue; }
                list.Add(new BindEntry
                {
                    Name = name, Guid = guid, DataType = Text(o, "dataType")?.Trim(), Group = group, Level = level,
                    Categories = cats, File = file, GroupTypeId = Text(o, "groupTypeId")?.Trim(), FileHash = Text(o, "fileHash")?.Trim(),
                });
            }
            return list;
        }

        internal sealed class RebindEntry
        {
            public string Name;
            public Guid OldGuid, Guid;
            public string File, FileHash, GroupTypeId;
            public string Level;                                    // null: keep the old binding's level
            public List<string> Categories = new List<string>();   // added to the old binding's categories
        }

        /// <summary>
        /// The request's rebind entries (explicit only): { name, oldGuid, guid, file, fileHash, categories, level,
        /// groupTypeId }. Both GUIDs and the file are required and the GUIDs must differ; others come back in problems.
        /// </summary>
        public static List<RebindEntry> ParseRebind(JsonArray entries, JsonArray problems)
        {
            var list = new List<RebindEntry>();
            foreach (var node in entries ?? new JsonArray())
            {
                if (node is not JsonObject o) { problems?.Add(new JsonObject { ["name"] = null, ["reason"] = "not a rebind object" }); continue; }
                var name = Text(o, "name")?.Trim();
                Guid oldGuid = Guid.Empty, guid = Guid.Empty;
                var oldOk = Guid.TryParse(Text(o, "oldGuid")?.Trim() ?? "", out oldGuid) && oldGuid != Guid.Empty;
                var newOk = Guid.TryParse(Text(o, "guid")?.Trim() ?? "", out guid) && guid != Guid.Empty;
                var file = Text(o, "file")?.Trim();
                var levelText = Text(o, "level");
                var level = string.IsNullOrWhiteSpace(levelText) ? null : ParameterFillLogic.LevelKey(levelText);
                if (level == "any") level = null;
                var cats = new List<string>();
                if (o["categories"] is JsonArray ca)
                    foreach (var n in ca) if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && !cats.Contains(s.Trim())) cats.Add(s.Trim());
                string reason = string.IsNullOrEmpty(name) ? "no parameter name"
                    : !oldOk ? "no valid oldGuid (the definition to replace)"
                    : !newOk ? "no valid guid: ACE binds only a definition from a shared parameter file, it never creates a GUID"
                    : oldGuid == guid ? "oldGuid and guid are the same: nothing to rebind"
                    : string.IsNullOrEmpty(file) ? "no shared parameter file: the definition comes only from a file"
                    : !string.IsNullOrWhiteSpace(levelText) && ParameterFillLogic.LevelKey(levelText) == null ? $"unknown level '{levelText}'"
                    : list.Any(r => r.OldGuid == oldGuid) ? "duplicate of an earlier rebind (same oldGuid)"
                    : null;
                if (reason != null) { problems?.Add(new JsonObject { ["name"] = name, ["reason"] = reason }); continue; }
                list.Add(new RebindEntry
                {
                    Name = name, OldGuid = oldGuid, Guid = guid, File = file, FileHash = Text(o, "fileHash")?.Trim(),
                    GroupTypeId = Text(o, "groupTypeId")?.Trim(), Level = level, Categories = cats,
                });
            }
            return list;
        }

        // ---- shared parameter files -----------------------------------------------------------------------------

        internal sealed class SharedParam
        {
            public Guid Guid;
            public string Name;
            public string DataType;
        }

        /// <summary>sha256 of the file's bytes, lower-case hex (as the server sends it).</summary>
        public static string FileHash(byte[] bytes)
        {
            using (var h = System.Security.Cryptography.SHA256.Create()) return Util.Compat.Hex(h.ComputeHash(bytes ?? new byte[0])).ToLowerInvariant();
        }

        /// <summary>The file's hash matches the expected one (none expected: true).</summary>
        public static bool HashMatches(string expected, byte[] bytes) =>
            string.IsNullOrWhiteSpace(expected) || string.Equals(expected.Trim(), FileHash(bytes), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The PARAM lines of a Revit shared parameter file (read without Revit, so a look-only plan never changes the
        /// shared parameter file setting): UTF-16 LE/BE by BOM or NUL bytes, else UTF-8; columns by the *PARAM header row.
        /// </summary>
        public static List<SharedParam> ReadSharedParameterFile(byte[] bytes)
        {
            var list = new List<SharedParam>();
            if (bytes == null || bytes.Length == 0) return list;
            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 400)) >= 0)
                text = bytes.Length > 1 && bytes[1] == 0 ? System.Text.Encoding.Unicode.GetString(bytes) : System.Text.Encoding.BigEndianUnicode.GetString(bytes);
            else
            {
                var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                text = System.Text.Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
            }
            int iGuid = 1, iName = 2, iType = 3;
            foreach (var raw in text.Split('\n'))
            {
                var cols = raw.TrimEnd('\r').Split('\t');
                var head = cols[0].Trim();
                if (head == "*PARAM")
                {
                    int At(string n, int d) { var i = Array.FindIndex(cols, c => string.Equals(c.Trim(), n, StringComparison.OrdinalIgnoreCase)); return i > 0 ? i : d; }
                    iGuid = At("GUID", 1); iName = At("NAME", 2); iType = At("DATATYPE", 3);
                    continue;
                }
                if (head != "PARAM") continue;
                string Col(int i) => i < cols.Length ? cols[i].Trim() : null;
                if (!Guid.TryParse(Col(iGuid) ?? "", out var g)) continue;
                list.Add(new SharedParam { Guid = g, Name = Col(iName), DataType = Col(iType) });
            }
            return list;
        }

        // ---- versions -------------------------------------------------------------------------------------------

        /// <summary>The server's expected version and the add-in's agree (a leading v and a fourth part are ignored).</summary>
        public static bool VersionMatches(string expected, string actual)
        {
            string Norm(string v)
            {
                v = (v ?? "").Trim().TrimStart('v', 'V');
                var parts = v.Split('.');
                return string.Join(".", parts.Take(3).Select(p => int.TryParse(p, out var n) ? n.ToString() : p));
            }
            return Norm(expected) == Norm(actual);
        }

        // ---- blocks and skips in plain language -----------------------------------------------------------------

        /// <summary>Why something was held or left, and what clears it, for a reason code (holder: who has it, where known).</summary>
        public static (string Why, string How) Explain(string code, string holder, string detail)
        {
            var who = string.IsNullOrWhiteSpace(holder) ? "another user" : holder.Trim();
            switch (code)
            {
                case "borrowed": return ($"borrowed by {who}", $"Ask {who} to synchronize with central and relinquish them, then run the plan again.");
                case "central": return ("changed in the central model since your last Reload Latest", "Reload Latest (Collaborate tab), then preview the plan again.");
                case "unreadable": return ("their worksharing state could not be read" + (string.IsNullOrEmpty(detail) ? "" : ": " + detail), "Check the connection to the central model, then run the plan again.");
                case "group": return ("in a model group: a change would reach every copy of the group", "Edit the group and set the value there, or leave it to the group's owner.");
                case "option": return ("in a design option", "Set the value in the design option by hand, or make the option primary first.");
                case "readonly": return ("the parameter is read-only here (a formula or the family drives it)", "Change it where it is driven (the family or the formula).");
                case "nottext": return ($"not a text parameter ({detail}): a text value cannot be stored", "Fill it by hand with a real value, or ask for the parameter to be a text parameter.");
                case "refused": return ("Revit refused the value", "Set one of them by hand to see Revit's message.");
                case "source-missing": return ($"'{detail}' is not a parameter of these elements", "Correct the derive rule in ace-parameter-mapping.json, or bind the parameter first.");
                case "source-blank": return ($"'{detail}' is blank", $"Fill '{detail}' first; a later run derives the value into targets that are blank or NA.");
                case "source-na": return ($"'{detail}' is declared missing (NA)", $"Fill '{detail}' with a real value; a later run derives the value into targets that are blank or NA.");
                case "derived-earlier": return ("an earlier derive rule already sets it", "Nothing to do; remove the duplicate rule from the mapping file if it is not wanted.");
                case "no-target": return ($"'{detail}' is not one of the requirement rows sent, so ACE has no elements to write", "Add the parameter to the requirement rows, or remove the rule.");
                case "version": return (detail ?? "the add-in and the server versions differ", "Install the same ACE version for the add-in and the server (build-package.ps1, then the installer), restart Revit, then run again.");
                case "other-guid": return (detail ?? "the same name is bound with another GUID", "A person decides which definition is right: remove the binding that is not current by hand, or correct the requirement's GUID. ACE does not change it.");
                case "guid-other-name": return (detail ?? "the model holds this GUID under another name", "A person decides: rename the parameter in the shared parameter file or the requirement. ACE does not change it.");
                case "not-in-file": return (detail ?? "the shared parameter file has no definition with this GUID", "Add the line to the project shared parameter file (or give the file that holds it), then run again.");
                case "file": return (detail ?? "the shared parameter file could not be opened", "Check the path and that the file is a Revit shared parameter file, then run again.");
                case "datatype": return (detail ?? "the file's data type differs from the requirement", "Correct the requirement or the shared parameter file; ACE binds the definition only as the file has it.");
                case "wrong-level": return (detail ?? "bound at the other level (type / instance)", "Changing the level discards the values already entered: a person decides, then rebinds it by hand.");
                case "category": return (detail ?? "a category cannot take bound parameters", "Correct the categories in the requirement or the mapping file.");
                case "built-in": return (detail ?? "a Revit built-in parameter cannot be bound", "Nothing to bind: record the elements that lack it as not available (the register lists them), or ask the client to drop the requirement for those categories.");
                case "file-changed": return (detail ?? "the shared parameter file changed since the preview", "Preview the plan again so it uses the file as it is now.");
                case "group-type": return (detail ?? "unknown parameter group", "Use a Revit parameter group name such as General, IdentityData or Data (or leave groupTypeId out for General).");
                case "rebind-entry": return (detail ?? "the rebind entry is incomplete", "Correct the entry (name, oldGuid, guid, shared parameter file), then run again.");
                case "old-missing": return (detail ?? "the old definition is not in this model", "Check the oldGuid against the model (parameter_check shows modelGuid); nothing to rebind otherwise.");
                case "rebind-level": return (detail ?? "the new binding would change type / instance", "Rebind keeps the level so values carry over; change the level by hand if a person decides to.");
                case "rebind-datatype": return (detail ?? "the old and new definitions have different data types", "Values cannot be carried over between data types: correct the shared parameter file or the requirement.");
                case "rebind-held": return (detail ?? "elements holding values cannot be edited now", "Clear the blockers listed (borrowed elements, Reload Latest), then run again.");
                case "bind-entry": return (detail ?? "the binding entry is incomplete", "Correct the entry (name, GUID, shared parameter file, categories), then run again.");
                case "derive-rule": return (detail ?? "the derive rule is incomplete", "Correct the rule in ace-parameter-mapping.json, then run again.");
                case "bind-failed":return (detail ?? "Revit did not accept the binding", "Bind it by hand (Manage > Project Parameters) to see Revit's message.");
                default: return (detail ?? code, "");
            }
        }

        /// <summary>One plain-language block or skip entry: { what, why, elements, sampleIds, holder, how } (+ code, name).</summary>
        public static JsonObject Entry(string what, string code, string holder, string detail, int elements, IEnumerable<long> sampleIds, string name = null)
        {
            var (why, how) = Explain(code, holder, detail);
            return new JsonObject
            {
                ["what"] = what,
                ["why"] = why,
                ["elements"] = elements,
                ["sampleIds"] = new JsonArray((sampleIds ?? Enumerable.Empty<long>()).Select(i => (JsonNode)i).ToArray()),
                ["holder"] = string.IsNullOrWhiteSpace(holder) ? null : holder.Trim(),
                ["how"] = how,
                ["code"] = code,
                ["name"] = name,
            };
        }

        internal sealed class Skip
        {
            public string Name;      // the parameter
            public string Code;      // see Explain
            public string Holder;
            public string Detail;
            public long Id;          // the element (or type); 0 when none
        }

        /// <summary>
        /// Skips grouped by parameter, reason and holder, largest first: "<name>: <n> element(s) <verb>" with up to
        /// maxIds sample ids each.
        /// </summary>
        public static JsonArray Group(IEnumerable<Skip> skips, string verb, int maxIds)
        {
            var groups = (skips ?? Enumerable.Empty<Skip>())
                .GroupBy(s => (s.Name ?? "", s.Code ?? "", s.Holder ?? "", s.Detail ?? ""))
                .Select(g => (Key: g.Key, Ids: g.Where(s => s.Id != 0).Select(s => s.Id).Distinct().ToList(), Count: g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal);
            var arr = new JsonArray();
            foreach (var g in groups)
            {
                var n = Math.Max(g.Ids.Count, g.Count);
                arr.Add(Entry($"{g.Key.Item1}: {n} element{(n == 1 ? "" : "s")} {verb}", g.Key.Item2, g.Key.Item3, g.Key.Item4.Length == 0 ? null : g.Key.Item4,
                    n, g.Ids.Take(Math.Max(0, maxIds)), g.Key.Item1));
            }
            return arr;
        }

        // ---- self-check -------------------------------------------------------------------------------------

        internal sealed class Expectation
        {
            public string Kind;       // derived | fill | binding
            public string Key;        // the row key (or the binding's name)
            public string Name;
            public long Id;           // the element written (0 for a binding)
            public string Expected;
            public string Actual;     // read again after the writes
            public bool Found = true; // false: the element or parameter is gone
            public string Why;        // a fixed reason: always a mismatch (the forced acceptance-test entry)
        }

        /// <summary>The extra mismatch test_force_verify_mismatch adds: the change is rolled back exactly as for a real one.</summary>
        public static Expectation ForcedMismatch() => new Expectation { Kind = "forced", Key = null, Name = null, Why = "forced by test_force_verify_mismatch (acceptance test)" };

        /// <summary>
        /// Every planned write must show after the writes: { ok, checked, mismatchCount, mismatches: [{ kind, key, name,
        /// id, expected, actual, why }] } (the first <paramref name="max"/> mismatches).
        /// </summary>
        public static JsonObject Verify(IList<Expectation> list, int max = 50)
        {
            var mismatches = new JsonArray();
            var count = 0;
            foreach (var x in list ?? new List<Expectation>())
            {
                string why = x.Why != null ? x.Why
                    : !x.Found ? "the element or parameter could not be read again"
                    : !string.Equals(x.Expected ?? "", x.Actual ?? "", StringComparison.Ordinal) ? "the value read again differs from the planned one"
                    : null;
                if (why == null) continue;
                count++;
                if (mismatches.Count < max)
                    mismatches.Add(new JsonObject
                    {
                        ["kind"] = x.Kind, ["key"] = x.Key, ["name"] = x.Name, ["id"] = x.Id == 0 ? null : (JsonNode)x.Id,
                        ["expected"] = x.Expected, ["actual"] = x.Actual, ["why"] = why,
                    });
            }
            return new JsonObject { ["ok"] = count == 0, ["checked"] = list?.Count ?? 0, ["mismatchCount"] = count, ["mismatches"] = mismatches };
        }

        // ---- impact -----------------------------------------------------------------------------------------

        /// <summary>
        /// How many sheets show annotation instances, from their owner views: an owner view that is a sheet counts itself
        /// (title blocks); any other view counts the sheets it is placed on (tags). Each sheet once.
        /// </summary>
        public static int SheetsShowing(IEnumerable<long> ownerViews, IDictionary<long, List<long>> viewToSheets, ICollection<long> sheets)
        {
            var seen = new HashSet<long>();
            foreach (var v in ownerViews ?? Enumerable.Empty<long>())
            {
                if (sheets != null && sheets.Contains(v)) seen.Add(v);
                else if (viewToSheets != null && viewToSheets.TryGetValue(v, out var on)) foreach (var s in on) seen.Add(s);
            }
            return seen.Count;
        }

        /// <summary>Whether a family's parameters stand for a label of the parameter: the shared GUID, or the name.</summary>
        public static bool LabelProxy(ICollection<Guid> familyGuids, ICollection<string> familyNames, Guid? guid, string name) =>
            (guid.HasValue && familyGuids != null && familyGuids.Contains(guid.Value))
            || (!string.IsNullOrEmpty(name) && familyNames != null && familyNames.Contains(name));

        private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
