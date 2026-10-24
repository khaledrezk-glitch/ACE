using System;
using System.Collections.Generic;
using System.Linq;
using AceRevitMcp.Util;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Ribbon
{
    /// <summary>
    /// Ribbon items that survive a payload swap. Revit can't remove ribbon items, so a build started by the ACE loader in a
    /// running Revit finds the panels and buttons the first build made (by name) and updates them instead of adding them
    /// again. Under the loader every button runs a loader proxy class (bindCommand maps it to this build's command by
    /// class name); loaded directly, a button runs this assembly's class as before.
    /// </summary>
    internal static class RibbonSlots
    {
        /// <summary>Button data for a command class of this assembly (full name), pointed at the loader's proxy when hosted.</summary>
        public static PushButtonData Button(string name, string text, string commandClass)
        {
            var bind = PayloadEntry.Get<Func<string, string>>("bindCommand");
            var loader = PayloadEntry.Get<string>("loaderPath");
            if (bind == null || loader == null) return new PushButtonData(name, text, PayloadEntry.AssemblyPath, commandClass);
            var proxy = bind(commandClass) ?? throw new InvalidOperationException($"The ACE loader has no free command slot for {commandClass}.");
            return new PushButtonData(name, text, loader, proxy);
        }

        /// <summary>
        /// The tab's panel of that name: the one this Revit session already has (kept in the loader's store, so it is found
        /// even if Revit leaves hidden panels out of GetRibbonPanels), else Revit's list, else a new one.
        /// </summary>
        public static RibbonPanel Panel(UIControlledApplication app, string tab, string name)
        {
            var store = PayloadEntry.Store;
            var key = "ribbon.panel." + tab + "|" + name;
            if (store.TryGetValue(key, out var kept) && kept is RibbonPanel known) return known;
            RibbonPanel existing = null;
            try { existing = app.GetRibbonPanels(tab).FirstOrDefault(p => p.Name == name); }
            catch { /* the tab does not exist yet */ }
            var panel = existing ?? app.CreateRibbonPanel(tab, name);
            store[key] = panel;
            return panel;
        }

        public static RibbonItem Find(RibbonPanel panel, string name)
        {
            try { return panel.GetItems().FirstOrDefault(i => i.Name == name); }
            catch { return null; }
        }

        /// <summary>The panel's button of that name, updated to this data; else the button added.</summary>
        public static RibbonItem Add(RibbonPanel panel, PushButtonData data)
        {
            var existing = Find(panel, data.Name);
            if (existing == null) return panel.AddItem(data);
            Refresh(existing, data);
            return existing;
        }

        /// <summary>One to three small buttons stacked (as AddStackedItems), or the existing ones updated.</summary>
        public static IList<RibbonItem> Stacked(RibbonPanel panel, IList<PushButtonData> items)
        {
            if (items == null || items.Count == 0) return null;
            var found = items.Take(3).Select(d => Find(panel, d.Name)).ToList();
            if (found.All(i => i != null))
            {
                for (var i = 0; i < found.Count; i++) Refresh(found[i], items[i]);
                return found;
            }
            if (found.Any(i => i != null))
            {
                // A stack can't be extended: a build that adds a button to one shows it after a Revit restart.
                Log.Warn($"Ribbon panel {panel.Name}: only some stacked buttons exist; the new ones appear after a Revit restart.");
                for (var i = 0; i < found.Count; i++) if (found[i] != null) Refresh(found[i], items[i]);
                return found;   // aligned with items (null where a button is missing)
            }
            if (items.Count == 1) return new List<RibbonItem> { panel.AddItem(items[0]) };
            if (items.Count == 2) return panel.AddStackedItems(items[0], items[1]);
            return panel.AddStackedItems(items[0], items[1], items[2]);
        }

        private static void Refresh(RibbonItem item, PushButtonData data)
        {
            try
            {
                item.ItemText = data.Text;
                item.ToolTip = data.ToolTip;
                item.LongDescription = data.LongDescription;
                if (item is RibbonButton b)
                {
                    if (data.LargeImage != null) b.LargeImage = data.LargeImage;
                    if (data.Image != null) b.Image = data.Image;
                }
                if (item is PushButton push)
                {
                    if (push.AssemblyName != data.AssemblyName) push.AssemblyName = data.AssemblyName;
                    if (push.ClassName != data.ClassName) push.ClassName = data.ClassName;
                }
            }
            catch (Exception ex) { Log.Warn($"Ribbon button {data.Name}: {ex.Message}"); }
        }
    }
}
