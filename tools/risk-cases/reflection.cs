// EXPECT_RISK: reflection reaching System.IO
var t = System.Type.GetType("System.IO." + "File");
return t?.GetMethod("Delete");
