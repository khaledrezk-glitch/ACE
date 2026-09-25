// @description: Copy (optionally transform) one parameter's value into another across every element of the given categories, e.g. Room "Name" -> "Comments" or Door "Mark" -> a shared parameter.
// @mode: auto
// @inputs: {"categories": ["Rooms"], "source": "Name", "target": "Comments", "prefix": "", "suffix": "", "only_if_target_empty": false, "use_display_value": true}

var categories = args["categories"] is JsonArray ca ? ca.Select(v => v.ToString()).ToList() : new List<string> { "Rooms" };
var sourceName = ctx.Str("source") ?? throw new Exception("'source' parameter name is required");
var targetName = ctx.Str("target") ?? throw new Exception("'target' parameter name is required");
var prefix = ctx.Str("prefix", "");
var suffix = ctx.Str("suffix", "");
var onlyIfEmpty = ctx.Bool("only_if_target_empty");
var useDisplay = ctx.Bool("use_display_value", true);

string Read(Parameter p) => p.StorageType switch
{
    StorageType.String => p.AsString(),
    StorageType.Integer => useDisplay ? p.AsValueString() : p.AsInteger().ToString(),
    StorageType.Double => useDisplay ? p.AsValueString() : p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
    StorageType.ElementId => useDisplay ? (doc.GetElement(p.AsElementId())?.Name ?? p.AsValueString()) : p.AsElementId().Value.ToString(),
    _ => null,
};

Parameter Find(Element e, string name) =>
    e.LookupParameter(name) ?? (Enum.TryParse<BuiltInParameter>(name, true, out var bip) ? e.get_Parameter(bip) : null);

int updated = 0, skippedEmpty = 0, skippedFilled = 0;
var problems = new List<object>();
foreach (var cat in categories)
{
    if (!Enum.TryParse<BuiltInCategory>("OST_" + cat.Replace(" ", ""), true, out var bic)) { problems.Add(new { category = cat, error = "unknown category" }); continue; }
    foreach (var e in ctx.Instances(bic))
    {
        var src = Find(e, sourceName);
        var dst = Find(e, targetName);
        if (src == null || dst == null) { problems.Add(new { id = e.Id.Value, error = src == null ? $"no '{sourceName}'" : $"no '{targetName}'" }); continue; }
        if (dst.IsReadOnly) { problems.Add(new { id = e.Id.Value, error = $"'{targetName}' is read-only" }); continue; }
        var value = Read(src);
        if (string.IsNullOrEmpty(value)) { skippedEmpty++; continue; }
        if (onlyIfEmpty && !string.IsNullOrEmpty(dst.AsValueString() ?? dst.AsString())) { skippedFilled++; continue; }
        var text = prefix + value + suffix;
        var ok = dst.StorageType == StorageType.String ? dst.Set(text) : dst.SetValueString(text);
        if (ok) updated++; else problems.Add(new { id = e.Id.Value, error = $"could not write '{text}'" });
    }
}

return new { updated, skippedEmpty, skippedFilled, problems = problems.Take(50).ToList(), problemCount = problems.Count };
