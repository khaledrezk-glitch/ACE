// @description: Fixes Revit's "identical instances in the same place" warnings: keeps the oldest element of each duplicate group and deletes the copies. Skips elements in use by colleagues (workshared). Preview first.
// @mode: auto
// @inputs: {"categories": ["optional category names to limit it to, e.g. Furniture"]}

var only = args["categories"] is JsonArray ca ? ca.Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
var groups = doc.GetWarnings()
    .Where(w => (w.GetDescriptionText() ?? "").IndexOf("identical instances in the same place", StringComparison.OrdinalIgnoreCase) >= 0)
    .Select(w => w.GetFailingElements().Select(id => doc.GetElement(id)).Where(e => e != null).OrderBy(e => e.Id.Value).ToList())
    .Where(g => g.Count > 1)
    .ToList();

var toDelete = new List<Element>();
var kept = new List<long>();
var seen = new HashSet<long>();
foreach (var g in groups)
{
    if (only != null && !only.Contains(g[0].Category?.Name ?? "")) continue;
    if (!seen.Add(g[0].Id.Value)) continue;   // a group can be reported more than once
    kept.Add(g[0].Id.Value);
    toDelete.AddRange(g.Skip(1).Where(e => seen.Add(e.Id.Value)));
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
};
