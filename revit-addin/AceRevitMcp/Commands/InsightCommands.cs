using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using AceRevitMcp.Bridge;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Commands
{
    /// <summary>
    /// Commands that make Claude smarter: look up the REAL Revit API installed on this machine
    /// (no guessing signatures), and discover what parameters and types a model actually has.
    /// </summary>
    internal static class InsightCommands
    {
        private static List<Type> _apiTypes;

        private static List<Type> ApiTypes()
        {
            if (_apiTypes != null) return _apiTypes;
            var list = new List<Type>();
            foreach (var asm in new[] { typeof(Document).Assembly, typeof(UIApplication).Assembly })
            {
                Type[] types;
                try { types = asm.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                list.AddRange(types);
            }
            return _apiTypes = list;
        }

        /// <summary>
        /// { "query": "Wall.Create" | "FilteredElementCollector" | "BuiltInCategory" | "OST_Door" | "ViewSheet.Create" }
        /// </summary>
        public static JsonNode ApiLookup(UIApplication app, JsonObject args)
        {
            var query = (Args.Str(args, "query") ?? "").Trim();
            if (query.Length < 2) throw new CommandException("'query' is required, e.g. \"Wall.Create\", \"ViewSheet\", \"BuiltInParameter.ALL_MODEL\".");
            var types = ApiTypes();

            string typePart = query, memberPart = null;
            var dot = query.LastIndexOf('.');
            if (dot > 0)
            {
                var head = query.Substring(0, dot);
                if (FindTypes(types, head).Count > 0) { typePart = head; memberPart = query.Substring(dot + 1); }
            }

            var matches = FindTypes(types, typePart);
            if (matches.Count == 0)
            {
                // Maybe it's a member or enum value name: search everywhere.
                var hits = new JsonArray();
                foreach (var t in types)
                {
                    if (t.IsEnum)
                    {
                        foreach (var n in Enum.GetNames(t).Where(n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Take(20))
                            hits.Add($"{t.Name}.{n}   (enum value)");
                    }
                    else
                    {
                        foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                     .Where(m => m.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).Take(5))
                            hits.Add($"{t.Name}.{m.Name}   ({m.MemberType})");
                    }
                    if (hits.Count >= 60) break;
                }
                var similar = types.Where(t => t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(t => t.Name.Length).Take(25).Select(t => (JsonNode)$"{t.FullName}").ToArray();
                return new JsonObject
                {
                    ["query"] = query,
                    ["exactType"] = false,
                    ["membersOrEnumValues"] = hits,
                    ["similarTypes"] = new JsonArray(similar),
                    ["hint"] = "Query a type name (e.g. \"Wall\") or Type.Member (e.g. \"Wall.Create\").",
                };
            }

            var result = new JsonArray();
            foreach (var t in matches.Take(3))
                result.Add(memberPart == null ? DescribeType(t) : DescribeMembers(t, memberPart));
            return new JsonObject { ["query"] = query, ["revitApi"] = app.Application.VersionBuild, ["results"] = result };
        }

        private static List<Type> FindTypes(List<Type> types, string name)
        {
            var exact = types.Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || (t.FullName?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            return exact;
        }

        private static JsonObject DescribeType(Type t)
        {
            var obj = new JsonObject
            {
                ["type"] = t.FullName,
                ["kind"] = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : "class",
            };
            if (t.BaseType != null && t.BaseType != typeof(object) && !t.IsEnum) obj["baseType"] = t.BaseType.Name;
            AddObsolete(obj, t);

            if (t.IsEnum)
            {
                var names = Enum.GetNames(t);
                obj["valueCount"] = names.Length;
                obj["values"] = new JsonArray(names.Take(400).Select(n => (JsonNode)n).ToArray());
                if (names.Length > 400) obj["note"] = "Truncated: query \"EnumName.partial\" to filter.";
                return obj;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            obj["constructors"] = new JsonArray(t.GetConstructors().Select(c => (JsonNode)Signature(c)).ToArray());
            obj["staticMethods"] = new JsonArray(t.GetMethods(flags).Where(m => m.IsStatic && !m.IsSpecialName).Select(m => (JsonNode)Signature(m)).Distinct().Take(80).ToArray());
            obj["methods"] = new JsonArray(t.GetMethods(flags).Where(m => !m.IsStatic && !m.IsSpecialName).Select(m => (JsonNode)Signature(m)).Distinct().Take(120).ToArray());
            obj["properties"] = new JsonArray(t.GetProperties(flags).Select(p => (JsonNode)PropertySignature(p)).Take(120).ToArray());
            var inherited = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.DeclaringType != t && m.DeclaringType != typeof(object) && !m.IsSpecialName)
                .Select(m => m.Name).Distinct().Take(60).ToArray();
            if (inherited.Length > 0) obj["inheritedMethodNames"] = new JsonArray(inherited.Select(n => (JsonNode)n).ToArray());
            return obj;
        }

        private static JsonObject DescribeMembers(Type t, string member)
        {
            if (t.IsEnum)
            {
                var values = Enum.GetNames(t).Where(n => n.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0).Take(200).Select(n => (JsonNode)n).ToArray();
                return new JsonObject { ["type"] = t.FullName, ["matchingValues"] = new JsonArray(values) };
            }

            var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name.Equals(member, StringComparison.OrdinalIgnoreCase) || m.Name.Equals("get_" + member, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var fuzzy = members.Count == 0;
            if (fuzzy)
                members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => m.Name.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0 && !(m is MethodInfo mi && mi.IsSpecialName)).Take(40).ToList();

            var arr = new JsonArray();
            foreach (var m in members)
            {
                var entry = new JsonObject
                {
                    ["signature"] = m switch
                    {
                        MethodBase mb => Signature(mb),
                        PropertyInfo p => PropertySignature(p),
                        FieldInfo f => $"{(f.IsStatic ? "static " : "")}{TypeName(f.FieldType)} {f.Name}",
                        EventInfo e => $"event {TypeName(e.EventHandlerType)} {e.Name}",
                        _ => m.Name,
                    },
                };
                if (m.DeclaringType != t) entry["declaredOn"] = m.DeclaringType?.Name;
                AddObsolete(entry, m);
                arr.Add(entry);
            }
            return new JsonObject
            {
                ["type"] = t.FullName,
                ["member"] = member,
                ["exact"] = !fuzzy,
                ["members"] = arr,
                ["note"] = arr.Count == 0 ? $"'{t.Name}' has no member like '{member}'. Query \"{t.Name}\" to list all members." : null,
            };
        }

        private static void AddObsolete(JsonObject obj, MemberInfo m)
        {
            var ob = m.GetCustomAttribute<ObsoleteAttribute>();
            if (ob != null) obj["obsolete"] = string.IsNullOrEmpty(ob.Message) ? "yes" : ob.Message;
        }

        private static string Signature(MethodBase m)
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{TypeName(p.ParameterType)} {p.Name}"));
            if (m is ConstructorInfo) return $"new {m.DeclaringType?.Name}({ps})";
            var mi = (MethodInfo)m;
            var generic = mi.IsGenericMethod ? "<" + string.Join(", ", mi.GetGenericArguments().Select(a => a.Name)) + ">" : "";
            var ob = m.GetCustomAttribute<ObsoleteAttribute>() != null ? "  [OBSOLETE]" : "";
            return $"{(m.IsStatic ? "static " : "")}{TypeName(mi.ReturnType)} {m.Name}{generic}({ps}){ob}";
        }

        private static string PropertySignature(PropertyInfo p)
        {
            var access = (p.CanRead ? "get; " : "") + (p.CanWrite && p.SetMethod?.IsPublic == true ? "set; " : "");
            var stat = (p.GetMethod ?? p.SetMethod)?.IsStatic == true ? "static " : "";
            var idx = p.GetIndexParameters();
            var name = idx.Length > 0 ? $"this[{string.Join(", ", idx.Select(i => TypeName(i.ParameterType)))}]" : p.Name;
            var ob = p.GetCustomAttribute<ObsoleteAttribute>() != null ? "  [OBSOLETE]" : "";
            return $"{stat}{TypeName(p.PropertyType)} {name} {{ {access}}}{ob}";
        }

        private static string TypeName(Type t)
        {
            if (t == null) return "void";
            if (t.IsByRef) return TypeName(t.GetElementType());
            if (t == typeof(void)) return "void";
            if (t == typeof(int)) return "int";
            if (t == typeof(long)) return "long";
            if (t == typeof(double)) return "double";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(object)) return "object";
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                var n = t.Name;
                var tick = n.IndexOf('`');
                if (tick > 0) n = n.Substring(0, tick);
                return $"{n}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
            }
            return t.Name;
        }

        /// <summary>
        /// Parameter schema of a category from real instances: which parameters exist, storage type,
        /// instance vs type, how often they're filled, sample values. { "category": "Doors", "sample": 300 }
        /// </summary>
        public static JsonNode DescribeCategory(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var catName = Args.Str(args, "category") ?? throw new CommandException("'category' is required, e.g. \"Doors\".");
            var cat = Args.ResolveCategory(doc, catName);
            var sampleSize = Math.Clamp(Args.Int(args, "sample", 300), 10, 5000);

            var all = new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType().ToElements();
            var sample = all.Count <= sampleSize ? all.ToList() : all.Where((e, i) => i % Math.Max(1, all.Count / sampleSize) == 0).Take(sampleSize).ToList();

            var stats = new Dictionary<string, ParamStat>();
            void Collect(Element e, bool isType)
            {
                foreach (Parameter p in e.Parameters)
                {
                    if (p.Definition == null) continue;
                    var key = (isType ? "T|" : "I|") + p.Definition.Name;
                    if (!stats.TryGetValue(key, out var s))
                        stats[key] = s = new ParamStat { Name = p.Definition.Name, IsType = isType, Storage = p.StorageType.ToString(), ReadOnly = p.IsReadOnly, Shared = p.IsShared };
                    s.Seen++;
                    var display = RevitJson.Safe(() => p.AsValueString()) ?? (p.StorageType == StorageType.String ? p.AsString() : null);
                    if (p.HasValue && !string.IsNullOrEmpty(display)) { s.Filled++; if (s.Samples.Count < 4) s.Samples.Add(display); }
                    if (p.StorageType == StorageType.Double) s.Spec ??= RevitJson.Safe(() => LabelUtils.GetLabelForSpec(p.Definition.GetDataType()));
                }
            }

            var typeIds = new HashSet<long>();
            foreach (var e in sample)
            {
                Collect(e, false);
                var tid = e.GetTypeId();
                if (tid != ElementId.InvalidElementId && typeIds.Add(tid.Value) && typeIds.Count <= 200 && doc.GetElement(tid) is ElementType t)
                    Collect(t, true);
            }

            JsonArray ToArray(bool type) => new JsonArray(stats.Values.Where(s => s.IsType == type).OrderBy(s => s.Name).Select(s =>
            {
                var o = new JsonObject
                {
                    ["name"] = s.Name,
                    ["storage"] = s.Storage,
                    ["filledPct"] = s.Seen == 0 ? 0 : Math.Round(100.0 * s.Filled / s.Seen),
                };
                if (s.Spec != null) o["spec"] = s.Spec;
                if (s.ReadOnly) o["readOnly"] = true;
                if (s.Shared) o["shared"] = true;
                if (s.Samples.Count > 0) o["samples"] = new JsonArray(s.Samples.Distinct().Select(v => (JsonNode)v).ToArray());
                return (JsonNode)o;
            }).ToArray());

            return new JsonObject
            {
                ["category"] = cat.Name,
                ["instances"] = all.Count,
                ["sampled"] = sample.Count,
                ["typesSampled"] = Math.Min(typeIds.Count, 200),
                ["instanceParameters"] = ToArray(false),
                ["typeParameters"] = ToArray(true),
                ["hint"] = "filledPct = share of sampled elements with a non-empty value. Type parameters are edited on the type (affect all instances).",
            };
        }

        private sealed class ParamStat
        {
            public string Name, Storage, Spec;
            public bool IsType, ReadOnly, Shared;
            public int Seen, Filled;
            public List<string> Samples = new List<string>();
        }

        /// <summary>Loaded family types / system types, optionally by category and name. Answers "what can I place?".</summary>
        public static JsonNode ListTypes(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            var limit = Math.Clamp(Args.Int(args, "limit", 300), 1, 3000);
            var collector = new FilteredElementCollector(doc).WhereElementIsElementType();
            var catName = Args.Str(args, "category");
            if (!string.IsNullOrEmpty(catName)) collector = collector.OfCategoryId(Args.ResolveCategory(doc, catName).Id);
            var contains = Args.Str(args, "name_contains");

            var used = new Dictionary<long, int>();
            if (Args.Bool(args, "count_instances", true))
            {
                var instances = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                if (!string.IsNullOrEmpty(catName)) instances = instances.OfCategoryId(Args.ResolveCategory(doc, catName).Id);
                foreach (var e in instances)
                {
                    var tid = e.GetTypeId().Value;
                    if (tid > 0) used[tid] = used.TryGetValue(tid, out var n) ? n + 1 : 1;
                }
            }

            var arr = new JsonArray();
            var total = 0;
            foreach (var t in collector.Cast<ElementType>().OrderBy(t => t.Category?.Name).ThenBy(t => t.FamilyName).ThenBy(t => t.Name))
            {
                var label = $"{t.FamilyName} {t.Name}";
                if (!string.IsNullOrEmpty(contains) && label.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.Category == null && string.IsNullOrEmpty(catName)) continue;
                total++;
                if (arr.Count >= limit) continue;
                var o = new JsonObject
                {
                    ["id"] = t.Id.Value,
                    ["category"] = t.Category?.Name,
                    ["family"] = t.FamilyName,
                    ["type"] = t.Name,
                    ["class"] = t.GetType().Name,
                    ["instances"] = used.TryGetValue(t.Id.Value, out var c) ? c : 0,
                };
                if (t is FamilySymbol fs)
                {
                    o["isActive"] = fs.IsActive;
                    o["placement"] = RevitJson.Safe(() => fs.Family.FamilyPlacementType.ToString());
                    if (fs.Family.IsInPlace) o["inPlace"] = true;
                }
                arr.Add(o);
            }
            return new JsonObject { ["total"] = total, ["returned"] = arr.Count, ["types"] = arr };
        }
    }
}
