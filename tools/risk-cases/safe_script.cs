// A normal script: no risk (GetType() on an object, Path, LINQ, a transaction-free read).
var walls = ctx.All<Wall>();
var name = System.IO.Path.GetFileName(doc.PathName ?? "");
return new { count = walls.Count, kind = walls.FirstOrDefault()?.GetType().Name, name };
