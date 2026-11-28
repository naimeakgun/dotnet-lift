using System.Xml.Linq;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Rewrites;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Analyzers;

/// <summary>
/// The legacy System.Data.SqlClient driver: where the code uses it, the package that brings it to modern .NET,
/// provider-name lookups that modern .NET cannot resolve, and what changes at runtime with Microsoft.Data.SqlClient
/// (connections are encrypted by default, EF 6's SQL Server provider stays on the old driver).
/// </summary>
public sealed class SqlClientAnalyzer : IAnalyzer
{
    public const string Category = "SQL client";
    public const string LegacyPackage = "System.Data.SqlClient";
    public const string ProviderName = "System.Data.SqlClient";

    /// <summary>The message prefix of the findings the sqlclient step resolves.</summary>
    public const string UsagePrefix = "System.Data.SqlClient";

    public string Name => "sql-client";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        var findings = new List<Finding>();
        foreach (var path in project.SourceFiles)
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
            var file = PathUtil.Relative(project.Directory, path);
            var usages = SqlClientRewrite.Find(root);
            if (usages.Count > 0)
            {
                findings.Add(new Finding(project.Name, Severity.Warning, Category,
                    $"System.Data.SqlClient is used ({usages.Count} reference{(usages.Count == 1 ? "" : "s")} in this file): the legacy SQL Server driver, on modern .NET only available as a deprecated package.",
                    "Move to Microsoft.Data.SqlClient: the type names are the same, dotnet-lift rewrites the namespaces and adds the package (it supports .NET Framework 4.6.2+ too).",
                    file, usages[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }

            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var method = call.Expression switch
                {
                    MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                    IdentifierNameSyntax i => i.Identifier.Text,
                    _ => null,
                };
                if (method == "GetFactory"
                    && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
                    && literal.Token.ValueText == ProviderName)
                {
                    findings.Add(new Finding(project.Name, Severity.Warning, Category,
                        "DbProviderFactories.GetFactory(\"System.Data.SqlClient\"): modern .NET has no machine.config, so no provider is registered under that name.",
                        "Register the factory at startup (DbProviderFactories.RegisterFactory(\"Microsoft.Data.SqlClient\", Microsoft.Data.SqlClient.SqlClientFactory.Instance)) and look it up by that name, or use SqlClientFactory.Instance directly.",
                        file, call.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }
            }
        }

        if (project.FindPackage(LegacyPackage) is { } package)
        {
            findings.Add(new Finding(project.Name, Severity.Warning, Category,
                $"System.Data.SqlClient package {package.Version}: deprecated, no new features or fixes beyond security.",
                "Replace it with Microsoft.Data.SqlClient (the sqlclient step does this)."));
        }

        var usesDriver = findings.Count > 0;
        if (!usesDriver)
        {
            return findings;
        }

        foreach (var (name, configFile) in ConnectionStringsWithoutEncrypt(project))
        {
            findings.Add(new Finding(project.Name, Severity.Info, Category,
                $"Connection string '{name}' does not set Encrypt: Microsoft.Data.SqlClient encrypts by default and refuses a server certificate the client does not trust.",
                "Decide per environment: a trusted certificate on the server (best), TrustServerCertificate=True, or Encrypt=False to keep today's behaviour. Test the connection on every environment before release.",
                configFile));
        }

        if (project.HasPackage("EntityFramework"))
        {
            findings.Add(new Finding(project.Name, Severity.Info, Category,
                "EF 6's SQL Server provider (EntityFramework.SqlServer) keeps using System.Data.SqlClient.",
                "Code that hands a SqlConnection to a DbContext must use the same driver as the provider: switch EF 6 to Microsoft.EntityFramework.SqlServer as well, or keep those files on System.Data.SqlClient."));
        }
        return findings;
    }

    /// <summary>SQL Server connection strings in App.config / Web.config without an Encrypt keyword.</summary>
    private static IEnumerable<(string Name, string File)> ConnectionStringsWithoutEncrypt(ProjectInfo project)
    {
        foreach (var path in new[] { project.AppConfigPath, project.WebConfigPath }.Where(p => p is not null && File.Exists(p)))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(path!);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            foreach (var add in doc.Descendants().Where(e => e.Name.LocalName == "connectionStrings").Elements().Where(e => e.Name.LocalName == "add"))
            {
                var value = add.Attribute("connectionString")?.Value ?? "";
                var provider = add.Attribute("providerName")?.Value;
                var sqlServer = provider is null || provider == ProviderName
                    ? value.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) || value.Contains("Server=", StringComparison.OrdinalIgnoreCase)
                    : false;
                if (sqlServer && !value.Contains("Encrypt", StringComparison.OrdinalIgnoreCase)
                    && !value.Contains("metadata=", StringComparison.OrdinalIgnoreCase)) // EF 6 EDMX strings: the inner string is EF's
                {
                    yield return (add.Attribute("name")?.Value ?? "(unnamed)", PathUtil.Relative(project.Directory, path!));
                }
            }
        }
    }
}
