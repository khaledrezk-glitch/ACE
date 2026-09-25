using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace AceRevitMcp.Compiler
{
    /// <summary>
    /// Entry point called by reflection from the add-in. Keep the signature primitive-only.
    /// </summary>
    public static class ScriptCompiler
    {
        /// <returns>object[] { byte[] assemblyOrNull, string[] errors, string[] warnings }</returns>
        public static object[] Compile(string source, string[] referencePaths, string assemblyName)
        {
            // An explicit encoding is required to emit (embedded) debug information.
            var tree = CSharpSyntaxTree.ParseText(
                SourceText.From(source, Encoding.UTF8),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: "script.cs");

            var references = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in referencePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                if (!seen.Add(Path.GetFileName(path))) continue;
                try { references.Add(MetadataReference.CreateFromFile(path)); }
                catch { /* unreadable or not a managed assembly: skip */ }
            }

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { tree },
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug,
                    allowUnsafe: false,
                    platform: Platform.X64,
                    nullableContextOptions: NullableContextOptions.Disable)
                    .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                    {
                        // The wrapper appends "return null;" after the user's code; ignore "unreachable code".
                        ["CS0162"] = ReportDiagnostic.Suppress,
                        // Missing XML comments / unused variables are noise for scripts.
                        ["CS0168"] = ReportDiagnostic.Suppress,
                        ["CS0219"] = ReportDiagnostic.Suppress,
                        ["CS8321"] = ReportDiagnostic.Suppress,
                    }));

            using var ms = new MemoryStream();
            // Embedded PDB => runtime exceptions carry line numbers of the user's code.
            var result = compilation.Emit(ms, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));

            string Format(Diagnostic d)
            {
                var span = d.Location.GetMappedLineSpan();
                var where = span.IsValid && d.Location.IsInSource
                    ? $"line {span.StartLinePosition.Line + 1}, col {span.StartLinePosition.Character + 1}: "
                    : "";
                return $"{where}{d.Id}: {d.GetMessage()}";
            }

            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(Format).ToArray();
            var warnings = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).Select(Format).Take(20).ToArray();

            return new object[] { result.Success ? ms.ToArray() : null, errors, warnings };
        }
    }
}
