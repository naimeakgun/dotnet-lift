using DotnetLift.Cli;
using DotnetLift.Core;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class SqlClientTests
{
    private const string Store = """
        using System.Data;
        using System.Data.SqlClient;
        using System.Data.SqlTypes;

        namespace Acme.Data
        {
            public class OrderStore
            {
                private readonly string _connectionString;

                public OrderStore(string connectionString) { _connectionString = connectionString; }

                public int Count()
                {
                    using (var connection = new SqlConnection(_connectionString))
                    using (var command = new System.Data.SqlClient.SqlCommand("select count(*) from Orders", connection))
                    {
                        connection.Open();
                        return (int)command.ExecuteScalar();
                    }
                }
            }
        }
        """;

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>An SDK-style net48 library with a data-access class, the old driver's package and an App.config connection string.</summary>
    private static string Library(string? profile = null, string packages = "", string? central = null)
    {
        var dir = NewDir();
        var lib = Path.Combine(dir, "Acme.Data");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "Acme.Data.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net48</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="System.Data.SqlClient"{(central is null ? " Version=\"4.9.0\"" : "")} />{packages}
              </ItemGroup>

            </Project>

            """);
        File.WriteAllText(Path.Combine(lib, "OrderStore.cs"), Store);
        File.WriteAllText(Path.Combine(lib, "App.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <connectionStrings>
                <add name="Orders" connectionString="Data Source=db01;Initial Catalog=Orders;Integrated Security=True" providerName="System.Data.SqlClient" />
                <add name="Reports" connectionString="Server=db02;Database=Reports;Integrated Security=True;Encrypt=False" />
              </connectionStrings>
            </configuration>
            """);
        if (central is not null)
        {
            File.WriteAllText(Path.Combine(dir, CentralPackageFile.FileName),
                "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n{central}\n  </ItemGroup>\n</Project>\n");
        }
        if (profile is not null)
        {
            File.WriteAllText(Path.Combine(dir, MigrationProfile.FileName), profile);
        }
        return dir;
    }

    [Fact]
    public void Namespaces_move_and_everything_else_stays()
    {
        const string source = """
            using System;
            using System.Data.Sql;
            using System.Data.SqlClient;
            using System.Data.SqlTypes;
            using Microsoft.Data.SqlClient;
            using Conn = System.Data.SqlClient.SqlConnection;

            class C
            {
                // System.Data.SqlClient in a comment stays
                const string Provider = "System.Data.SqlClient";
                global::System.Data.SqlClient.SqlCommand A() => new System.Data.SqlClient.SqlCommand();
                object B() => System.Data.SqlClient.SqlClientFactory.Instance;
                SqlString D() => SqlString.Null;
            }
            """;

        var result = SqlClientRewrite.Rewrite(source)!;
        var text = result.NewText;

        Assert.Equal(6, result.Replaced);
        Assert.Contains("using Microsoft.Data.Sql;", text);
        Assert.Contains("using System.Data.SqlTypes;", text); // shared by both drivers
        Assert.Single(text.Split('\n'), l => l.Trim() == "using Microsoft.Data.SqlClient;"); // the duplicate is gone
        Assert.Contains("using Conn = Microsoft.Data.SqlClient.SqlConnection;", text);
        Assert.Contains("global::Microsoft.Data.SqlClient.SqlCommand A() => new Microsoft.Data.SqlClient.SqlCommand();", text);
        Assert.Contains("object B() => Microsoft.Data.SqlClient.SqlClientFactory.Instance;", text);
        Assert.Contains("// System.Data.SqlClient in a comment stays", text);
        Assert.Contains("const string Provider = \"System.Data.SqlClient\";", text);
        Assert.DoesNotContain("System.Data.SqlClient.", text.Replace("\"System.Data.SqlClient\"", ""));
    }

    [Fact]
    public void Files_without_the_old_driver_are_left_alone()
    {
        Assert.Null(SqlClientRewrite.Rewrite("using System.Data; using System.Data.SqlTypes; class C { }"));
        Assert.Null(SqlClientRewrite.Rewrite("using Microsoft.Data.SqlClient; class C { }"));
    }

    [Fact]
    public void Analyze_reports_usages_the_package_connection_strings_without_Encrypt_and_provider_lookups()
    {
        var dir = Library();
        File.AppendAllText(Path.Combine(dir, "Acme.Data", "OrderStore.cs"), """

            namespace Acme.Data
            {
                static class Factories
                {
                    static System.Data.Common.DbProviderFactory Get() => System.Data.Common.DbProviderFactories.GetFactory("System.Data.SqlClient");
                }
            }
            """);

        var findings = new SolutionAnalyzer().Analyze(dir).Findings.Where(f => f.Category == SqlClientAnalyzer.Category).ToList();

        var usage = Assert.Single(findings, f => f.Message.StartsWith("System.Data.SqlClient is used"));
        Assert.Contains("(2 references in this file)", usage.Message);
        Assert.Equal("OrderStore.cs", usage.File);
        Assert.Equal(2, usage.Line);
        Assert.Single(findings, f => f.Message.StartsWith("System.Data.SqlClient package 4.9.0"));
        Assert.Single(findings, f => f.Message.StartsWith("DbProviderFactories.GetFactory"));
        var encrypt = Assert.Single(findings, f => f.Message.Contains("does not set Encrypt"));
        Assert.Contains("'Orders'", encrypt.Message); // Reports sets Encrypt=False explicitly
        Assert.Equal("App.config", encrypt.File);
    }

    [Fact]
    public void Projects_without_the_old_driver_get_no_findings()
    {
        var findings = new SolutionAnalyzer().Analyze(Samples.LegacyShopSln).Findings;

        Assert.DoesNotContain(findings, f => f.Category == SqlClientAnalyzer.Category);
    }

    [Fact]
    public void The_step_asks_by_default_is_approved_by_the_profile_and_skipped_with_keep()
    {
        Assert.Null(Plan(Library()).Argument);
        Assert.Equal(Risk.High, Plan(Library()).Risk);
        Assert.Equal(SqlClientMigration.ApprovedArgument, Plan(Library("""{ "sqlClient": "microsoft" }""")).Argument);

        var keep = Library("""{ "sqlClient": "keep" }""");
        Assert.DoesNotContain(UpgradePlanner.Create(new SolutionAnalyzer().Analyze(keep), MigrationProfile.Find(keep)).Steps, s => s.Id == "sqlclient");

        var steps = UpgradePlanner.Create(new SolutionAnalyzer().Analyze(Library()), MigrationProfile.Default).Steps.Select(s => s.Id).ToList();
        Assert.True(steps.IndexOf("sqlclient") < steps.IndexOf("retarget"), "the driver moves while the project still builds for .NET Framework");

        static UpgradeStep Plan(string dir) =>
            Assert.Single(UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir), MigrationProfile.Find(dir)).Steps, s => s.Id == "sqlclient");
    }

    [Fact]
    public void The_step_rewrites_the_code_and_swaps_the_package()
    {
        var dir = Library();
        var project = SolutionLoader.Load(dir).Projects.Single();
        var step = UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir), MigrationProfile.Default).Steps.Single(s => s.Id == "sqlclient");

        var changes = new SqlClientMigration().Prepare(project, step, "net10.0");

        var code = Assert.Single(changes, c => c.Path.EndsWith("OrderStore.cs", StringComparison.Ordinal)).NewContent!;
        Assert.Contains("using Microsoft.Data.SqlClient;", code);
        Assert.Contains("new Microsoft.Data.SqlClient.SqlCommand(", code);
        var csproj = Assert.Single(changes, c => c.Path.EndsWith(".csproj", StringComparison.Ordinal)).NewContent!;
        Assert.DoesNotContain("System.Data.SqlClient", csproj);
        Assert.Contains($"<PackageReference Include=\"{SqlClientMigration.PackageId}\" Version=\"{SqlClientMigration.PackageVersion}\" />", csproj);
        Assert.DoesNotContain("Condition", csproj); // every target
    }

    [Fact]
    public void Under_central_package_management_a_pinned_version_wins()
    {
        var dir = Library(central: "    <PackageVersion Include=\"System.Data.SqlClient\" Version=\"4.9.0\" />\n    <PackageVersion Include=\"Microsoft.Data.SqlClient\" Version=\"6.1.8\" />");
        var project = SolutionLoader.Load(dir).Projects.Single();
        var step = UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir), MigrationProfile.Default).Steps.Single(s => s.Id == "sqlclient");

        var changes = new SqlClientMigration().Prepare(project, step, "net10.0");

        var csproj = Assert.Single(changes, c => c.Path.EndsWith(".csproj", StringComparison.Ordinal)).NewContent!;
        Assert.Contains("<PackageReference Include=\"Microsoft.Data.SqlClient\" />", csproj);
        Assert.DoesNotContain("VersionOverride", csproj);
        Assert.DoesNotContain(changes, c => c.Path.EndsWith(CentralPackageFile.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unattended_upgrade_leaves_the_driver_until_approved()
    {
        var dir = Library();
        Git.InitWithCommit(dir);
        var output = new StringWriter();

        await new App(new StringReader(""), output, interactive: false, new FakeBuilder()).RunAsync(["upgrade", dir, "--yes"]);

        Assert.Contains("Move from System.Data.SqlClient to Microsoft.Data.SqlClient", output.ToString());
        Assert.Contains("encrypts connections by default", output.ToString());
        Assert.Contains("using System.Data.SqlClient;", File.ReadAllText(Path.Combine(dir, "Acme.Data", "OrderStore.cs")));
    }

    [Fact]
    public async Task With_the_team_decision_in_the_profile_upgrade_moves_the_driver()
    {
        var dir = Library("""{ "target": "net10.0", "sqlClient": "microsoft" }""");
        Git.InitWithCommit(dir);

        await new App(new StringReader(""), new StringWriter(), interactive: false, new FakeBuilder()).RunAsync(["upgrade", dir, "--yes"]);

        Assert.Contains("using Microsoft.Data.SqlClient;", File.ReadAllText(Path.Combine(dir, "Acme.Data", "OrderStore.cs")));
        Assert.Contains("dotnet-lift: Move from System.Data.SqlClient to Microsoft.Data.SqlClient (Acme.Data)", Git.LogSubjects(dir));
        var left = new SolutionAnalyzer().Analyze(dir).Findings.Where(f => f.Category == SqlClientAnalyzer.Category).ToList();
        Assert.Empty(left); // no usages, no package: the connection-string note goes with them
    }
}
