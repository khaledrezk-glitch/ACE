// @description: Fixes Revit's "identical instances in the same place" warnings: keeps the oldest element of each duplicate group and deletes the copies. Skips elements in use by colleagues (workshared). Preview first.
// @mode: auto
// @inputs: {"categories": ["optional category names to limit it to, e.g. Furniture"]}

var only = args["categories"] is JsonArray ca ? ca.Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
var groups = doc.GetWarnings()
    .Where(w => (w.GetDescriptionText() ?? "").IndexOf("identical instances in the same place", StringComparison.OrdinalIgnoreCase) >= 0)
    .Select(w => w.GetFailingElements().Select(id => doc.GetElement(id)).Where(e => e != null).OrderBy(e => e.Id.Value).ToList())
    .Where(g => g.Count > 1)
    .ToList();

// Never delete a copy that hosts something (a door in a wall would go with it) or that is part of a model group.
var hosts = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
    .Where(fi => fi.Host != null).Select(fi => fi.Host.Id.Value));
bool Removable(Element e) => !hosts.Contains(e.Id.Value) && (e.GroupId == null || e.GroupId == ElementId.InvalidElementId);

var toDelete = new List<Element>();
var kept = new List<long>();
var protectedCopies = new List<long>();
var done = new HashSet<long>();
foreach (var g in groups)
{
    if (only != null && !only.Contains(g[0].Category?.Name ?? "")) continue;
    var members = g.Where(e => !done.Contains(e.Id.Value)).ToList();   // overlapping reports of one group: each element once
    if (members.Count == 0) continue;
    // Keep the copy that cannot be removed (hosts something or is grouped), else the oldest.
    var keep = members.FirstOrDefault(e => !Removable(e)) ?? members[0];
    kept.Add(keep.Id.Value);
    foreach (var e in members)
    {
        done.Add(e.Id.Value);
        if (e.Id == keep.Id) continue;
        if (Removable(e)) toDelete.Add(e); else protectedCopies.Add(e.Id.Value);
    }
}
var deletable = ctx.Editable(toDelete);
var deleted = deletable.Count == 0 ? new List<ElementId>() : doc.Delete(deletable.Select(e => e.Id).ToList()).ToList();

return new
{
    duplicateGroups = kept.Count,
    copiesDeleted = deletable.Count,
    elementsRemovedInTotal = deleted.Count,   // includes anything hosted on the copies (e.g. tags)
    byCategory = deletable.GroupBy(e => e.Category?.Name ?? "?").ToDictionary(g => g.Key, g => g.Count()),
    keptIds = kept.Take(50),
    notDeleted = protectedCopies.Count == 0 ? null : $"{protectedCopies.Count} copies host other elements or are in groups and were left: check them by hand (ids {string.Join(", ", protectedCopies.Take(20))})",
};
