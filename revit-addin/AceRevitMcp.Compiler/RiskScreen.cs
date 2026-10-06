using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AceRevitMcp.Compiler
{
    /// <summary>
    /// Finds what a script really calls, by the compiler's resolved symbols rather than by its text, so aliases,
    /// fully qualified names, comments, escaped characters or "var" cannot hide a call to files, programs, the network,
    /// reflection, or saving / closing / syncing models. Each risk needs the user's consent (allow_risky).
    /// The MCP server's text screen stays as a quick first check.
    /// </summary>
    internal static class RiskScreen
    {
        private static readonly (string Prefix, string Why)[] Namespaces =
        {
            ("System.IO.Compression", "creates, changes or deletes files on disk"),
            ("System.Net", "uses the network"),
            ("System.Reflection", "uses reflection to reach code beyond the Revit API"),
            ("System.Runtime.Loader", "loads external code"),
            ("System.Runtime.InteropServices", "loads external code"),
            ("Microsoft.Win32", "changes Windows settings"),
        };

        private static readonly (string Type, string Why)[] Types =
        {
            ("System.IO.File", "reads or changes files on disk"), ("System.IO.Directory", "reads or changes files on disk"),
            ("System.IO.FileInfo", "reads or changes files on disk"), ("System.IO.DirectoryInfo", "reads or changes files on disk"),
            ("System.IO.FileStream", "reads or changes files on disk"), ("System.IO.StreamWriter", "creates, changes or deletes files on disk"),
            ("System.IO.StreamReader", "reads files on disk"), ("System.IO.BinaryWriter", "creates, changes or deletes files on disk"),
            ("System.IO.DriveInfo", "reads or changes files on disk"),
            ("System.Diagnostics.Process", "starts other programs"), ("System.Diagnostics.ProcessStartInfo", "starts other programs"),
            ("System.Activator", "uses reflection to reach code beyond the Revit API"),
            ("System.AppDomain", "loads external code"),
            ("System.IO.MemoryMappedFiles.MemoryMappedFile", "reads or changes files on disk"),
        };

        private static readonly (string Type, string Member, string Why)[] Members =
        {
            ("System.Environment", "Exit", "closes programs"), ("System.Environment", "FailFast", "closes programs"),
            ("System.Type", "GetType", "uses reflection to reach code beyond the Revit API"),   // the static Type.GetType(string)
            ("System.Type", "InvokeMember", "uses reflection to reach code beyond the Revit API"),
            ("System.Type", "GetMethod", "uses reflection to reach code beyond the Revit API"),
            ("System.Type", "GetMethods", "uses reflection to reach code beyond the Revit API"),
            ("System.Type", "GetField", "uses reflection to reach code beyond the Revit API"),
            ("System.Type", "GetProperty", "uses reflection to reach code beyond the Revit API"),
            ("System.Type", "GetConstructor", "uses reflection to reach code beyond the Revit API"),
            ("Autodesk.Revit.DB.Document", "Save", "saves or closes a model"), ("Autodesk.Revit.DB.Document", "SaveAs", "saves or closes a model"),
            ("Autodesk.Revit.DB.Document", "SaveCloudModel", "saves or closes a model"), ("Autodesk.Revit.DB.Document", "Close", "saves or closes a model"),
            ("Autodesk.Revit.DB.Document", "SynchronizeWithCentral", "syncs with the central model"),
            ("Autodesk.Revit.DB.Document", "ReloadLatest", "syncs with the central model"),
            ("Autodesk.Revit.DB.Document", "Export", "exports files from the model"), ("Autodesk.Revit.DB.Document", "ExportImage", "exports files from the model"),
            ("Autodesk.Revit.DB.WorksharingUtils", "RelinquishOwnership", "syncs with the central model"),
            ("Autodesk.Revit.DB.RevitLinkType", "Unload", "opens other models or unloads links"),
            ("Autodesk.Revit.DB.RevitLinkType", "UnloadLocally", "opens other models or unloads links"),
            ("Autodesk.Revit.DB.RevitLinkType", "LoadFrom", "opens other models or unloads links"),
            ("Autodesk.Revit.ApplicationServices.Application", "OpenDocumentFile", "opens other models or unloads links"),
            ("Autodesk.Revit.UI.UIApplication", "OpenAndActivateDocument", "opens other models or unloads links"),
            ("Autodesk.Revit.UI.UIApplication", "PostCommand", "triggers Revit commands"),
            ("Autodesk.Revit.UI.UIDocument", "SaveAndClose", "saves or closes a model"),
            ("Autodesk.Revit.UI.UIDocument", "SaveAs", "saves or closes a model"),
            ("Autodesk.Revit.ApplicationServices.Application", "CopyModel", "creates, changes or deletes files on disk"),
            ("Autodesk.Revit.DB.TransmissionData", "WriteTransmissionData", "creates, changes or deletes files on disk"),
            ("Autodesk.Revit.DB.PrintManager", "SubmitPrint", "prints or writes files"),
            ("Autodesk.Revit.DB.DefinitionGroups", "Create", "changes the shared parameter file"),
            ("Autodesk.Revit.DB.Definitions", "Create", "changes the shared parameter file"),
            ("System.IO.Path", "GetTempFileName", "creates, changes or deletes files on disk"),
            ("System.Xml.XmlDocument", "Save", "creates, changes or deletes files on disk"),
            ("System.Xml.Linq.XDocument", "Save", "creates, changes or deletes files on disk"),
            ("System.Xml.Linq.XElement", "Save", "creates, changes or deletes files on disk"),
            ("System.Xml.XmlWriter", "Create", "creates, changes or deletes files on disk"),
        };

        public static string[] Find(CSharpCompilation compilation, SyntaxTree tree)
        {
            var model = compilation.GetSemanticModel(tree);
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                    or MemberAccessExpressionSyntax or IdentifierNameSyntax)) continue;
                var info = model.GetSymbolInfo(node);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                var type = model.GetTypeInfo(node).Type;
                if (type?.TypeKind == TypeKind.Dynamic) found.Add("uses dynamic code that cannot be checked");
                if (symbol == null) continue;
                var why = Check(symbol);
                if (why != null) found.Add(why);
            }
            return found.ToArray();
        }

        private static string Check(ISymbol symbol)
        {
            var owner = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            if (owner == null) return null;
            var typeName = Name(owner.OriginalDefinition);
            var ns = owner.ContainingNamespace?.ToDisplayString() ?? "";
            // Reading a property (e.GetType().Name) is harmless; calling or creating something is what counts.
            var readsOnly = symbol is IPropertySymbol or IFieldSymbol;
            foreach (var (prefix, why) in Namespaces)
                if (!readsOnly && (ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal))) return why;
            foreach (var (t, why) in Types)
                if (typeName == t || InheritsFrom(owner, t)) return why;
            if (symbol is IMethodSymbol m)
            {
                foreach (var (t, member, why) in Members)
                {
                    if (m.Name != member) continue;
                    if (t == "System.Type" && member == "GetType" && !m.IsStatic) continue;   // obj.GetType() is harmless
                    if (typeName == t || InheritsFrom(owner, t)) return why;
                }
            }
            return null;
        }

        private static bool InheritsFrom(INamedTypeSymbol type, string name)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
                if (Name(t.OriginalDefinition) == name) return true;
            return false;
        }

        private static string Name(INamedTypeSymbol t) =>
            t.ContainingNamespace == null || t.ContainingNamespace.IsGlobalNamespace ? t.MetadataName : $"{t.ContainingNamespace.ToDisplayString()}.{t.MetadataName}";
    }
}
