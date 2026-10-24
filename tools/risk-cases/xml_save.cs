// EXPECT_RISK: XML written to a file
var x = new System.Xml.XmlDocument();
x.LoadXml("<a/>");
x.Save("C:/temp/a.xml");
return null;
