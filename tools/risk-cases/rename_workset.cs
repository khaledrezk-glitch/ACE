// EXPECT_RISK: renaming a workset escapes the dry run's rollback (not undoable)
var ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).FirstOrDefault();
if (ws != null) WorksetTable.RenameWorkset(doc, ws.Id, "TST_RENAMED");
return null;
