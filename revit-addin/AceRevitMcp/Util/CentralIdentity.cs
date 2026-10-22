using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Util
{
    /// <summary>
    /// The Revit side of <see cref="CentralIdentityLogic"/> (#38): reads a workshared model's central path and identity
    /// (Document.WorksharingCentralGUID, in the 2024 and 2025 API; Revit documents it for server-based centrals, so for a
    /// file-based central the episode GUID of BasicFileInfo.LatestCentralEpisodeGUID is compared too), compares it with the
    /// other open models and the per-machine record, and records what it saw. Used by open_model, save_model save_as central
    /// and the model check. Never changes a model.
    /// </summary>
    internal static class CentralIdentity
    {
        private static readonly object Gate = new object();
        private static string StorePath => Path.Combine(AceConfig.Directory, CentralIdentityLogic.FileName);

        /// <summary>A model's central and identity; null when it is not workshared, is detached (no central), cloud, a link or a family.</summary>
        public static CentralId Of(Document doc)
        {
            try
            {
                if (doc == null || doc.IsLinked || doc.IsFamilyDocument || !doc.IsWorkshared || doc.IsModelInCloud) return null;
                var mp = doc.GetWorksharingCentralModelPath();
                var central = mp == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                if (string.IsNullOrWhiteSpace(central)) return null;
                string cg = null, eg = null;
                try { cg = CentralIdentityLogic.Guid(doc.WorksharingCentralGUID.ToString()); } catch { cg = null; }
                try
                {
                    var file = doc.PathName;
                    if (!string.IsNullOrEmpty(file) && File.Exists(file)) eg = CentralIdentityLogic.Guid(BasicFileInfo.Extract(file).LatestCentralEpisodeGUID.ToString());
                }
                catch { eg = null; }
                return new CentralId { Path = central, Model = doc.Title, CentralGuid = cg, EpisodeGuid = eg };
            }
            catch { return null; }
        }

        /// <summary>
        /// The warnings for this model (none when it shares its identity with no other central), comparing it with the other
        /// open models and the stored records whose file still exists (a central moved away leaves no warning); with remember,
        /// the open models' identities are recorded.
        /// </summary>
        public static List<string> Check(Document doc, bool remember = true)
        {
            var warnings = new List<string>();
            var me = Of(doc);
            if (me == null) return warnings;
            var open = new List<CentralId>();
            try
            {
                foreach (Document d in doc.Application.Documents)
                    if (!d.Equals(doc) && Of(d) is CentralId o) open.Add(o);
            }
            catch { /* the other models cannot be read: the record still counts */ }
            lock (Gate)
            {
                var stored = Load();
                var existing = stored.Where(s => { try { return File.Exists(s.Path); } catch { return false; } }).ToList();
                foreach (var (other, shared, where) in CentralIdentityLogic.Clashes(me, open, existing))
                    warnings.Add(CentralIdentityLogic.Warning(me, other, shared, where));
                if (remember) Save(CentralIdentityLogic.Remember(stored, new[] { me }.Concat(open), DateTime.Now));
            }
            return warnings;
        }

        private static List<CentralId> Load()
        {
            try { return File.Exists(StorePath) ? CentralIdentityLogic.Parse(File.ReadAllText(StorePath)) : new List<CentralId>(); }
            catch (Exception ex) { Log.Warn($"{CentralIdentityLogic.FileName} could not be read: {ex.Message}"); return new List<CentralId>(); }
        }

        private static void Save(List<CentralId> records)
        {
            try
            {
                Directory.CreateDirectory(AceConfig.Directory);
                var tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, CentralIdentityLogic.Serialize(records));
                Compat.MoveOverwrite(tmp, StorePath);
            }
            catch (Exception ex) { Log.Warn($"{CentralIdentityLogic.FileName} could not be written: {ex.Message}"); }
        }
    }
}
