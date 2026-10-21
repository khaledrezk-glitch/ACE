// EXPECT_RISK: reloading a link from another file needs no open transaction (mode no_transaction) and is not undoable
var link = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().First();
link.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(@"C:\Temp\TST_Link.rvt"), new WorksetConfiguration());
return null;
