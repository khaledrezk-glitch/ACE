// EXPECT_RISK: a readonly run or dry run must not write files outside the ACE output folder (not undoable)
System.IO.File.WriteAllText(@"C:\Temp\TST_report.txt", "x");
return null;
