using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// The material of a structural element, shared by the clash engine's material filter and clash_resolve:
    /// steel | concrete | precast | timber | masonry | other | unknown. The family's structural material type first, then the
    /// structural material's class (family instance: StructuralMaterialId; walls, floors: the compound structure's
    /// structural layer), then the category (walls, floors, foundations and stairs are concrete in a structural model).
    /// </summary>
    internal static class StructuralMaterial
    {
        public static readonly string[] Names = { "steel", "concrete", "precast", "timber", "masonry", "other", "unknown" };

        private static readonly HashSet<string> ConcreteByCategory = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_Walls", "OST_Floors", "OST_StructuralFoundation", "OST_Stairs",
        };

        private static readonly HashSet<string> IgnoredClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "", "generic", "unassigned", "default", "none", "other",
        };

        /// <summary>The plain rule (no Revit types, tested in tools/clash-test).</summary>
        public static string Classify(string category, string structuralType, string materialClass)
        {
            switch ((structuralType ?? "").Trim().ToLowerInvariant())
            {
                case "steel": return "steel";
                case "concrete": return "concrete";
                case "precastconcrete": return "precast";
                case "wood": return "timber";
                case "aluminum": return "other";
            }
            var mc = (materialClass ?? "").Trim();
            if (!IgnoredClasses.Contains(mc))
            {
                var l = mc.ToLowerInvariant();
                if (l.Contains("concrete")) return l.Contains("precast") ? "precast" : "concrete";
                if (l.Contains("metal") || l.Contains("steel")) return "steel";
                if (l.Contains("wood") || l.Contains("timber")) return "timber";
                if (l.Contains("masonry") || l.Contains("brick") || l.Contains("block") || l.Contains("stone")) return "masonry";
                return "other";
            }
            return category != null && ConcreteByCategory.Contains(category) ? "concrete" : "unknown";
        }

        /// <summary>
        /// Whether an element of this material takes part: an excluded material never; with an include list, the listed ones
        /// and "unknown" (unknown is kept unless it is excluded); without one, everything not excluded.
        /// </summary>
        public static bool Keep(string material, ICollection<string> include, ICollection<string> exclude)
        {
            material = material ?? "unknown";
            if (exclude != null && exclude.Contains(material)) return false;
            if (include == null || include.Count == 0) return true;
            return include.Contains(material) || material == "unknown";
        }

        /// <summary>Validated, lower-case material names; an unknown name throws with the allowed ones.</summary>
        public static string[] Parse(IEnumerable<string> values, string field)
        {
            var list = new List<string>();
            foreach (var v in values ?? Enumerable.Empty<string>())
            {
                var n = (v ?? "").Trim().ToLowerInvariant();
                if (n == "wood") n = "timber";
                if (n == "precastconcrete") n = "precast";
                if (!Names.Contains(n)) throw new Bridge.CommandException($"{field}: '{v}' is not a material. Use {string.Join(", ", Names)}.");
                if (!list.Contains(n)) list.Add(n);
            }
            return list.ToArray();
        }

        /// <summary>The element's material in its own document.</summary>
        public static string Of(Element e)
        {
            var doc = e.Document;
            string type = null, cls = null;
            try
            {
                var mid = ElementId.InvalidElementId;
                if (e is FamilyInstance fi)
                {
                    type = fi.StructuralMaterialType.ToString();
                    mid = fi.StructuralMaterialId;
                }
                else if (doc.GetElement(e.GetTypeId()) is HostObjAttributes hoa && hoa.GetCompoundStructure() is CompoundStructure cs && cs.StructuralMaterialIndex >= 0)
                    mid = cs.GetMaterialId(cs.StructuralMaterialIndex);
                if (mid != ElementId.InvalidElementId && doc.GetElement(mid) is Material mat) cls = mat.MaterialClass;
            }
            catch { /* unknown: by category */ }
            return Classify(e.Category?.BuiltInCategory.ToString(), type, cls);
        }
    }
}
