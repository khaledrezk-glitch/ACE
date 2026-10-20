// Not a file write: a StreamWriter on a MemoryStream, a StringWriter, and an XML document saved to a TextWriter (no risk expected).
using (var ms = new System.IO.MemoryStream())
using (var sw = new System.IO.StreamWriter(ms))
{
    sw.WriteLine("TST");
    sw.Flush();
}
var text = new System.IO.StringWriter();
var xml = new System.Xml.XmlDocument();
xml.LoadXml("<a/>");
xml.Save(text);
return text.ToString();
