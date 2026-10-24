// @description: QA check: for chosen categories and parameters, report how many elements have each parameter filled, per level, with ids of the missing ones. Read-only.
// @mode: readonly
// @inputs: {"categories": ["Doors", "Rooms"], "parameters": ["Mark", "Comments"], "max_ids": 50}

var categories = args["categories"] is JsonArray ca ? ca.Select(v => v.ToString()).ToList() : new List<string> { "Doors" };
var parameters = args["parameters"] is JsonArray pa ? pa.Select(v => v.ToString()).ToList() : new List<string> { "Mark" };
var maxIds = (int)ctx.Num("max_ids", 50);

Parameter Find(Element e, string name)
{
    var p = e.LookupParameter(name) ?? (Enum.TryParse<BuiltInParameter>(name, true, out var bip) ? e.get_Parameter(bip) : null);
    if (p == null && doc.GetElement(e.GetTypeId()) is ElementType t)
        p = t.LookupParameter(name) ?? (Enum.TryParse<BuiltInParameter>(name, true, out var bip2) ? t.get_Parameter(bip2) : null);
    return p;
}

bool Filled(Parameter p) => p != null && p.HasValue && !string.IsNullOrWhiteSpace(p.StorageType == StorageType.String ? p.AsString() : p.AsValueString());

var levelNames = ctx.Levels().ToDictionary(l => l.Id.Value, l => l.Name);
var report = new List<object>();
foreach (var cat in categories)
{
    if (!Enum.TryParse<BuiltInCategory>("OST_" + cat.Replace(" ", ""), true, out var bic)) { report.Add(new { category = cat, error = "unknown category" }); continue; }
    var elements = ctx.Instances(bic);
    foreach (var pname in parameters)
    {
        var missing = elements.Where(e => !Filled(Find(e, pname))).ToList();
        var absent = elements.Count(e => Find(e, pname) == null);
        report.Add(new
        {
            category = cat,
            parameter = pname,
            total = elements.Count,
            filled = elements.Count - missing.Count,
            filledPct = elements.Count == 0 ? 100 : Math.Round(100.0 * (elements.Count - missing.Count) / elements.Count, 1),
            parameterDoesNotExistOn = absent,
            missingByLevel = missing.GroupBy(e => levelNames.TryGetValue(e.LevelId.Value, out var n) ? n : "(no level)")
                .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            missingIds = missing.Take(maxIds).Select(e => e.Id.Value).ToList(),
        });
    }
}
return report;
