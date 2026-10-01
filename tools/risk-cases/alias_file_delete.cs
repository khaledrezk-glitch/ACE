// EXPECT_RISK: an alias must not hide File.Delete
using F = System.IO.File;
F.Delete("C:/temp/x.txt");
return null;
