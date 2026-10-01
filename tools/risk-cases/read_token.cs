// EXPECT_RISK: reading files (e.g. the bridge token)
return System.IO.File.ReadAllText(System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ACE-RevitMCP", "config.json"));
