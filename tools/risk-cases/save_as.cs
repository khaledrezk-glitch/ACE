// EXPECT_RISK: Save As writes a file and escapes the dry run's rollback (not undoable)
doc.SaveAs(@"C:\Temp\TST_copy.rvt", new SaveAsOptions { OverwriteExistingFile = true });
return null;
