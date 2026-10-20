// EXPECT_RISK: File.WriteAllText writes to disk (not undoable)
System.IO.File.WriteAllText(@"C:\Temp\TST_out.txt", "TST");
return null;
