using System.Xml.Linq;
using DotnetLift.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Analyzers;

/// <summary>
/// One finding per project for things that are counted rather than fixed line by line: COM references in the project file,
/// Enterprise Library packages and references, and how much of System.Drawing the code uses (Windows-only on modern .NET).
/// </summary>
public sealed class PlatformInventoryAnalyzer : IAnalyzer
{
    private static readonly string[] DrawingTypes =
        ["Bitmap", "Image", "Graphics", "Font", "Brush", "Brushes", "SolidBrush", "Pen", "Pens", "Icon", "ImageFormat", "Metafile", "StringFormat"];

    public string Name => "platform-inventory";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        var com = ComReferences(project);
        if (com.Count > 0)
        {
            yield return new Finding(project.Name, Severity.Warning, "Windows-only",
                $"{com.Count} COM reference(s) in the project file: {string.Join(", ", com)}.",
                "COM interop works on modern .NET on Windows only. The SDK builds COMReference items with the .NET Framework MSBuild (Visual Studio / msbuild.exe), not `dotnet build`: generate the interop assembly once and reference it, or use a primary interop assembly package.",
                Path.GetFileName(project.FullPath));
        }

        var enterpriseLibrary = project.Packages.Select(p => p.Id)
            .Concat(project.AssemblyReferences)
            .Where(id => id.StartsWith("EnterpriseLibrary.", StringComparison.OrdinalIgnoreCase)
                         || id.StartsWith("Microsoft.Practices.EnterpriseLibrary", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (enterpriseLibrary.Count > 0)
        {
            yield return new Finding(project.Name, Severity.Warning, "Dependency",
                $"Enterprise Library: {string.Join(", ", enterpriseLibrary)}.",
                "Most blocks have no modern .NET version: plan a replacement per block (Data → ADO.NET/Dapper, Logging → Microsoft.Extensions.Logging, Caching → IMemoryCache, Validation → DataAnnotations).");
        }

        if (DrawingUsage(project) is { Files: > 0 } drawing)
        {
            var types = drawing.Types.Count == 0
                ? ""
                : ": " + string.Join(", ", drawing.Types.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key} ×{t.Value}"));
            yield return new Finding(project.Name, Severity.Warning, "Windows-only",
                $"System.Drawing is used in {drawing.Files} file(s){types}.",
                "System.Drawing.Common runs on Windows only since .NET 7. Fine for Windows hosting (add the package for the modern target); for Linux, move image work to ImageSharp or SkiaSharp. Color, Point, Size and Rectangle are cross-platform and are not counted.");
        }
    }

    private static List<string> ComReferences(ProjectInfo project)
    {
        try
        {
            return XDocument.Load(project.FullPath).Descendants()
                .Where(e => e.Name.LocalName == "COMReference")
                .Select(e => e.Attribute("Include")?.Value ?? "?")
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private sealed record Drawing(int Files, Dictionary<string, int> Types);

    /// <summary>Files that import or qualify System.Drawing, and how often they name its Windows-only types.</summary>
    private static Drawing DrawingUsage(ProjectInfo project)
    {
        var files = 0;
        var types = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in project.SourceFiles)
        {
            var text = File.ReadAllText(path);
            if (!text.Contains("System.Drawing", StringComparison.Ordinal))
            {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var imports = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Any(u => u.Name?.ToString() is { } n && (n == "System.Drawing" || n.StartsWith("System.Drawing.", StringComparison.Ordinal)));
            var qualified = root.DescendantNodes().OfType<QualifiedNameSyntax>()
                .Any(q => q.Left.ToString() == "System.Drawing" || q.Left.ToString().StartsWith("System.Drawing.", StringComparison.Ordinal));
            var memberAccess = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Any(m => m.Expression.ToString() == "System.Drawing");
            if (!imports && !qualified && !memberAccess)
            {
                continue;
            }

            files++;
            foreach (var name in root.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text))
            {
                if (Array.IndexOf(DrawingTypes, name) >= 0)
                {
                    types[name] = types.GetValueOrDefault(name) + 1;
                }
            }
        }
        return new Drawing(files, types);
    }
}
