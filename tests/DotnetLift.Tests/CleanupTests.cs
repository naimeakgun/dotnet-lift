using DotnetLift.Cli;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class CleanupTests
{
    /// <summary>
    /// A folder with one project "A": legacy or SDK-style, the inner XML of its item groups, an optional packages.config
    /// body and extra files (relative path → content).
    /// </summary>
    private static string Project(bool sdk, string items, string? packages = null, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "A"));
        var xml = sdk
            ? $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net48</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n"
            : $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n    <OutputType>Library</OutputType>\n  </PropertyGroup>\n  <ItemGroup>\n    <Reference Include=\"System\" />\n{items}\n  </ItemGroup>\n</Project>\n";
        File.WriteAllText(Path.Combine(dir, "A", "A.csproj"), xml);
        if (packages is not null)
        {
            File.WriteAllText(Path.Combine(dir, "A", "packages.config"),
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<packages>\n{packages}\n</packages>\n");
        }
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, "A", path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    private static string Ref(string assembly) =>
        $"    <Reference Include=\"{assembly}, Version=1.0.0.0, Culture=neutral, processorArchitecture=MSIL\">\n      <HintPath>..\\packages\\{assembly}.1.0.0\\lib\\net45\\{assembly}.dll</HintPath>\n    </Reference>";

    private static string Package(string id) => $"  <package id=\"{id}\" version=\"1.0.0\" targetFramework=\"net48\" />";

    /// <summary>Line endings normalized: on Windows the XML writer turns the "\n" in whitespace into "\r\n".</summary>
    private static string Read(string dir, string path) => File.ReadAllText(Path.Combine(dir, "A", path)).Replace("\r\n", "\n");

    private const string PlainCode = "using System;\nnamespace A { public class Thing { public DateTime When { get; set; } } }\n";

    [Fact]
    public void Removes_an_unused_library_from_the_project_file_and_packages_config()
    {
        var dir = Project(sdk: false,
            Ref("Unity.Abstractions") + "\n" + Ref("Unity.Container") + "\n" + Ref("Newtonsoft.Json"),
            Package("Unity") + "\n" + Package("Newtonsoft.Json"),
            ("Thing.cs", PlainCode));

        var plan = Cleanup.Plan(SolutionLoader.Load(dir));
        FileChange.ApplyAll(plan.ReferenceChanges);

        var item = Assert.Single(plan.Removed);
        Assert.Equal("Unity (dependency injection)", item.Library);
        Assert.Equal(new[] { "reference Unity.Abstractions", "reference Unity.Container", "package Unity" }, item.References);
        var csproj = Read(dir, "A.csproj");
        Assert.DoesNotContain("Unity", csproj);
        Assert.Contains("<Reference Include=\"Newtonsoft.Json, Version=1.0.0.0", csproj);
        Assert.Contains("<Reference Include=\"System\" />\n    <Reference Include=\"Newtonsoft.Json", csproj); // no blank line left behind
        var packages = Read(dir, "packages.config");
        Assert.DoesNotContain("Unity", packages);
        Assert.Contains("<package id=\"Newtonsoft.Json\"", packages);
    }

    [Theory]
    [InlineData("using Microsoft.Practices.Unity;\nnamespace A { class B { } }", "using")]
    [InlineData("using IoC = Unity;\nnamespace A { class B { } }", "alias")]
    [InlineData("namespace A { class B { object C = new global::Unity.UnityContainer(); } }", "global::")]
    [InlineData("namespace A { class B { void C() { Unity.Bootstrapper.Start(); } } }", "member access")]
    [InlineData("namespace A { class B : Unity.Extension.UnityContainerExtension { } }", "qualified base type")]
    public void Any_use_of_a_namespace_keeps_the_library(string code, string why)
    {
        var dir = Project(sdk: false, Ref("Unity.Container"), files: ("B.cs", code));

        var plan = Cleanup.Plan(SolutionLoader.Load(dir));

        Assert.True(plan.Removed.Count == 0, why);
        var kept = Assert.Single(plan.Kept);
        Assert.Contains("is used in B.cs", kept.KeptBecause);
        Assert.Empty(plan.ReferenceChanges);
    }

    [Fact]
    public void A_namespace_that_only_ends_with_the_same_word_does_not_count()
    {
        var dir = Project(sdk: false, Ref("System.EnterpriseServices"),
            files: ("B.cs", "using Contoso.System.EnterpriseServices;\nnamespace Contoso.Owin { class B { } }"));

        var plan = Cleanup.Plan(SolutionLoader.Load(dir));

        Assert.Equal("System.EnterpriseServices (COM+)", Assert.Single(plan.Removed).Library);
    }

    [Fact]
    public void Config_files_count_but_binding_redirects_do_not()
    {
        var bindingOnly = Project(sdk: false, Ref("Microsoft.Owin") + "\n" + Ref("Owin"), files:
        [
            ("Thing.cs", PlainCode),
            ("App.config", """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <runtime>
                    <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
                      <dependentAssembly>
                        <assemblyIdentity name="Microsoft.Owin" publicKeyToken="31bf3856ad364e35" culture="neutral" />
                        <bindingRedirect oldVersion="0.0.0.0-4.2.2.0" newVersion="4.2.2.0" />
                      </dependentAssembly>
                    </assemblyBinding>
                  </runtime>
                </configuration>
                """),
        ]);
        var section = Project(sdk: false, Ref("Microsoft.Practices.Unity") + "\n" + Ref("Microsoft.Practices.Unity.Configuration"), files:
        [
            ("Thing.cs", PlainCode),
            ("App.config", """
                <configuration>
                  <configSections>
                    <section name="unity" type="Microsoft.Practices.Unity.Configuration.UnityConfigurationSection, Microsoft.Practices.Unity.Configuration" />
                  </configSections>
                </configuration>
                """),
        ]);

        Assert.Equal("OWIN / Katana", Assert.Single(Cleanup.Plan(SolutionLoader.Load(bindingOnly)).Removed).Library);
        var kept = Assert.Single(Cleanup.Plan(SolutionLoader.Load(section)).Kept);
        Assert.Equal("Microsoft.Practices.Unity is used in App.config", kept.KeptBecause);
    }

    [Fact]
    public void Views_keep_mvc_and_bundling()
    {
        var dir = Project(sdk: false, Ref("System.Web.Mvc") + "\n" + Ref("System.Web.Optimization"), files:
        [
            ("Thing.cs", PlainCode),
            ("Views/Shared/_Layout.cshtml", "<html><head>@Styles.Render(\"~/Content/css\")</head><body>@RenderBody()</body></html>"),
        ]);

        var plan = Cleanup.Plan(SolutionLoader.Load(dir));

        Assert.Empty(plan.Removed);
        Assert.Collection(plan.Kept,
            mvc => Assert.Equal("Razor views use it (Views/Shared/_Layout.cshtml)", mvc.KeptBecause),
            bundling => Assert.Equal("\"Styles.Render\" is used in Views/Shared/_Layout.cshtml", bundling.KeptBecause));
    }

    [Fact]
    public void A_package_that_builds_on_the_library_keeps_it()
    {
        var dir = Project(sdk: true,
            "    <PackageReference Include=\"Microsoft.Owin\" Version=\"4.2.2\" />\n    <PackageReference Include=\"Microsoft.AspNet.Identity.Owin\" Version=\"2.2.4\" />",
            files: ("Thing.cs", PlainCode));

        var kept = Assert.Single(Cleanup.Plan(SolutionLoader.Load(dir)).Kept);

        Assert.Equal("Microsoft.AspNet.Identity.Owin probably depends on it", kept.KeptBecause);
    }

    [Fact]
    public void Sdk_style_package_references_and_language_packs_go_together()
    {
        var dir = Project(sdk: true,
            "    <PackageReference Include=\"Microsoft.AspNet.Mvc\" Version=\"5.2.9\" />\n" +
            "    <PackageReference Include=\"Microsoft.AspNet.Mvc.tr\" Version=\"5.2.9\" />\n" +
            "    <PackageReference Include=\"Microsoft.AspNet.Mvc.zh-Hans\" Version=\"5.2.9\" />\n" +
            "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />",
            files: ("Thing.cs", PlainCode));

        var plan = Cleanup.Plan(SolutionLoader.Load(dir));
        FileChange.ApplyAll(plan.ReferenceChanges);

        Assert.Equal(new[] { "package Microsoft.AspNet.Mvc", "package Microsoft.AspNet.Mvc.tr", "package Microsoft.AspNet.Mvc.zh-Hans" },
            Assert.Single(plan.Removed).References);
        var csproj = Read(dir, "A.csproj");
        Assert.DoesNotContain("Mvc", csproj);
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", csproj);
    }

    [Fact]
    public void A_global_using_item_in_the_project_file_keeps_the_library()
    {
        var dir = Project(sdk: true,
            "    <PackageReference Include=\"Unity\" Version=\"5.11.10\" />\n    <Using Include=\"Unity\" />",
            files: ("Thing.cs", PlainCode));

        Assert.Equal("Unity is used in A.csproj", Assert.Single(Cleanup.Plan(SolutionLoader.Load(dir)).Kept).KeptBecause);
    }

    [Fact]
    public void Sample_web_project_loses_only_its_unused_unity_references()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);

        var plan = Cleanup.Plan(SolutionLoader.Load(Path.Combine(dir, "LegacyShop.sln")));
        FileChange.ApplyAll(plan.ReferenceChanges);

        var removed = Assert.Single(plan.Removed);
        Assert.Equal("LegacyShop.Web", removed.Project);
        Assert.Equal(new[] { "reference Unity.Mvc5", "package Unity", "package Unity.Mvc5" }, removed.References);
        Assert.Contains(plan.Kept, k => k.Library == "ASP.NET MVC" && k.KeptBecause!.StartsWith("System.Web.Mvc is used in"));
        Assert.Contains(plan.Kept, k => k.Library.StartsWith("ASP.NET Web Optimization") && k.KeptBecause!.StartsWith("System.Web.Optimization is used in"));

        var web = Path.Combine(dir, "LegacyShop.Web");
        Assert.DoesNotContain("Unity", File.ReadAllText(Path.Combine(web, "LegacyShop.Web.csproj")));
        Assert.Contains("System.Web.Mvc", File.ReadAllText(Path.Combine(web, "LegacyShop.Web.csproj")));
        Assert.DoesNotContain("Unity", File.ReadAllText(Path.Combine(web, "packages.config")));
        Assert.Contains("Microsoft.AspNet.Mvc", File.ReadAllText(Path.Combine(web, "packages.config")));
        Assert.Equal(2, plan.ReferenceChanges.Count);
    }

    [Fact]
    public void Loader_ignores_project_copies_in_migration_backups()
    {
        var dir = Project(sdk: true, "", files: ("Thing.cs", PlainCode));
        Directory.CreateDirectory(Path.Combine(dir, "MigrationBackup", "1a2b3c4d", "A"));
        File.Copy(Path.Combine(dir, "A", "A.csproj"), Path.Combine(dir, "MigrationBackup", "1a2b3c4d", "A", "A.csproj"));
        File.WriteAllText(Path.Combine(dir, "A", "MigrationBackup.cs"), PlainCode.Replace("Thing", "Other")); // a file, not the folder

        var solution = SolutionLoader.Load(dir);

        var project = Assert.Single(solution.Projects);
        Assert.Equal(2, project.SourceFiles.Count);
    }

    [Fact]
    public async Task Cli_removes_references_and_backups_in_separate_commits()
    {
        var dir = Project(sdk: false, Ref("Unity.Container"), Package("Unity"), ("Thing.cs", PlainCode));
        var backup = Path.Combine(dir, "MigrationBackup", "1a2b3c4d", "A");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "A.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(backup, "packages.config"), "<packages />");
        File.WriteAllText(Path.Combine(backup, "NuGetUpgradeLog.html"), "<html></html>");
        Git.InitWithCommit(dir);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["cleanup", dir, "--yes"]);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("✓ A: Unity (dependency injection) (reference Unity.Container, package Unity)", text);
        Assert.Contains("✓ MigrationBackup/ (3 file(s))", text);
        Assert.Equal(
            new[] { "dotnet-lift: Remove Visual Studio migration backups", "dotnet-lift: Remove unused references", "initial" },
            Git.LogSubjects(dir));
        Assert.Equal("", Git.Status(dir));
        Assert.False(Directory.Exists(Path.Combine(dir, "MigrationBackup")));
        Assert.DoesNotContain("Unity", Read(dir, "A.csproj"));
    }

    [Fact]
    public async Task Dry_run_shows_the_diff_and_writes_nothing()
    {
        var dir = Project(sdk: false, Ref("Unity.Container"), files: ("Thing.cs", PlainCode));
        Directory.CreateDirectory(Path.Combine(dir, "_UpgradeReport_Files"));
        File.WriteAllText(Path.Combine(dir, "_UpgradeReport_Files", "UpgradeReport.css"), "body {}");
        var before = Read(dir, "A.csproj");
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["cleanup", dir, "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.Contains("-    <Reference Include=\"Unity.Container", output.ToString());
        Assert.Contains("✓ _UpgradeReport_Files/ (1 file(s))", output.ToString());
        Assert.Contains("Dry run: nothing was written.", output.ToString());
        Assert.Equal(before, Read(dir, "A.csproj"));
        Assert.True(File.Exists(Path.Combine(dir, "_UpgradeReport_Files", "UpgradeReport.css")));
    }

    [Fact]
    public async Task Nothing_to_do_says_so()
    {
        var dir = Project(sdk: true, "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", files: ("Thing.cs", PlainCode));
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["cleanup", dir]);

        Assert.Equal(0, exit);
        Assert.Contains("Nothing to clean up.", output.ToString());
    }
}
