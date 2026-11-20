using DotnetLift.Cli;
using DotnetLift.Core;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class RefsTests
{
    /// <summary>
    /// A solution folder with SolutionItems/*.dll and projects that reference them by HintPath.
    /// Each project: name, SDK-style or not, the inner XML of its item groups.
    /// </summary>
    private static string Solution(params (string Name, bool Sdk, string Items)[] projects)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "SolutionItems"));
        foreach (var dll in new[] { "Acme.Logging.dll", "Acme.Core.dll", "Vendor.Sdk.dll" })
        {
            File.WriteAllBytes(Path.Combine(dir, "SolutionItems", dll), [0x4D, 0x5A, 0x90, 0x00]);
        }

        foreach (var (name, sdk, items) in projects)
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
            var xml = sdk
                ? $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net48</TargetFramework>\n  </PropertyGroup>\n{items}\n</Project>\n"
                : $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n    <OutputType>Library</OutputType>\n  </PropertyGroup>\n{items}\n</Project>\n";
            File.WriteAllText(Path.Combine(dir, name, name + ".csproj"), xml);
        }
        return dir;
    }

    private static string Ref(string assembly, string version = "2.3.0.0") =>
        $"    <Reference Include=\"{assembly}, Version={version}, Culture=neutral, PublicKeyToken=null\">\n      <HintPath>..\\SolutionItems\\{assembly}.dll</HintPath>\n    </Reference>";

    private static string Group(params string[] items) => "  <ItemGroup>\n" + string.Join("\n", items) + "\n  </ItemGroup>";

    private static string Csproj(string dir, string name) => File.ReadAllText(Path.Combine(dir, name, name + ".csproj"));

    [Fact]
    public void Loader_records_file_references_and_skips_packages_folder_ones()
    {
        var dir = Solution(("A", true, Group(Ref("Acme.Logging"),
            "    <Reference Include=\"Newtonsoft.Json\">\n      <HintPath>..\\packages\\Newtonsoft.Json.13.0.3\\lib\\net45\\Newtonsoft.Json.dll</HintPath>\n    </Reference>")));

        var project = SolutionLoader.Load(dir).Projects.Single();

        var reference = Assert.Single(project.FileReferences);
        Assert.Equal("Acme.Logging", reference.AssemblyName);
        Assert.Equal("2.3.0.0", reference.AssemblyVersion);
        Assert.Equal(Path.Combine(dir, "SolutionItems", "Acme.Logging.dll"), reference.FullPath);
    }

    [Fact]
    public void By_name_turns_the_reference_into_a_package_and_notes_the_unused_dll()
    {
        var dir = Solution(("A", true, Group(Ref("Acme.Logging"))));

        var plan = FileReferenceConversion.Plan(SolutionLoader.Load(dir), PackageMap.Empty, byName: true);
        FileChange.ApplyAll(plan.Changes);

        var csproj = Csproj(dir, "A");
        Assert.Contains("<PackageReference Include=\"Acme.Logging\" Version=\"2.3.0\" />", csproj);
        Assert.DoesNotContain("HintPath", csproj);
        Assert.Equal("A: Acme.Logging → Acme.Logging 2.3.0", Assert.Single(plan.Converted));
        Assert.Contains(plan.Notes, n => n.StartsWith("SolutionItems/Acme.Logging.dll is no longer referenced"));
        Assert.True(File.Exists(Path.Combine(dir, "SolutionItems", "Acme.Logging.dll"))); // never deleted by the tool
    }

    [Fact]
    public void The_map_decides_ids_versions_and_what_to_keep()
    {
        var dir = Solution(("A", true, Group(Ref("Acme.Logging"), Ref("Acme.Core", "1.0.0.0"), Ref("Vendor.Sdk"))));
        File.WriteAllText(Path.Combine(dir, PackageMap.FileName), """
            {
              // comments and trailing commas are fine
              "Acme.Logging": "Acme.Diagnostics@3.1.0",
              "Acme.Core": "Acme.Core",
              "Vendor.Sdk": "keep",
            }
            """);

        var plan = FileReferenceConversion.Plan(SolutionLoader.Load(dir), PackageMap.Load(Path.Combine(dir, PackageMap.FileName)), byName: false);
        FileChange.ApplyAll(plan.Changes);

        var csproj = Csproj(dir, "A");
        Assert.Contains("<PackageReference Include=\"Acme.Diagnostics\" Version=\"3.1.0\" />", csproj);
        Assert.Contains("<PackageReference Include=\"Acme.Core\" Version=\"1.0.0\" />", csproj);
        Assert.Contains("Vendor.Sdk.dll</HintPath>", csproj);
        Assert.Empty(plan.Unmapped);
        Assert.DoesNotContain(plan.Notes, n => n.Contains("Vendor.Sdk"));
    }

    [Fact]
    public void Without_map_or_by_name_nothing_changes_and_the_dlls_are_listed()
    {
        var dir = Solution(("A", true, Group(Ref("Acme.Logging"))));
        var before = Csproj(dir, "A");

        var plan = FileReferenceConversion.Plan(SolutionLoader.Load(dir), PackageMap.Empty, byName: false);

        Assert.Empty(plan.Changes);
        Assert.Equal("A: Acme.Logging (..\\SolutionItems\\Acme.Logging.dll)", Assert.Single(plan.Unmapped));
        Assert.Equal(before, Csproj(dir, "A"));
    }

    [Fact]
    public void Conditional_references_keep_their_condition()
    {
        var dir = Solution(("A", true,
            "  <ItemGroup Condition=\"'$(TargetFramework)' == 'net48'\">\n" + Ref("Acme.Logging") + "\n  </ItemGroup>"));

        FileChange.ApplyAll(FileReferenceConversion.Plan(SolutionLoader.Load(dir), PackageMap.Empty, byName: true).Changes);

        var csproj = Csproj(dir, "A");
        var group = csproj[csproj.IndexOf("<ItemGroup Condition=\"'$(TargetFramework)' == 'net48'\">", StringComparison.Ordinal)..];
        Assert.Contains("<PackageReference Include=\"Acme.Logging\" Version=\"2.3.0\" />", group);
    }

    [Fact]
    public void Under_CPM_the_version_goes_to_Directory_Packages_props()
    {
        var dir = Solution(("A", true, Group(Ref("Acme.Logging"))), ("B", true, Group(Ref("Acme.Logging"))));
        File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"),
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n  </ItemGroup>\n</Project>\n");

        var plan = FileReferenceConversion.Plan(SolutionLoader.Load(dir), PackageMap.Empty, byName: true);
        FileChange.ApplyAll(plan.Changes);

        Assert.Equal(3, plan.Changes.Count); // two projects and one props file
        Assert.Contains("<PackageReference Include=\"Acme.Logging\" />", Csproj(dir, "B"));
        var props = File.ReadAllText(Path.Combine(dir, "Directory.Packages.props"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(props, "PackageVersion Include=\"Acme.Logging\""));
    }

    [Fact]
    public void Projects_on_packages_config_are_left_alone()
    {
        var dir = Solution(("Legacy", false, Group(Ref("Acme.Logging"))));
        File.WriteAllText(Path.Combine(dir, "Legacy", "packages.config"), "<packages />");

        var plan = FileReferenceConversion.Plan(SolutionLoader.Load(dir), PackageMap.Empty, byName: true);

        Assert.Empty(plan.Changes);
        Assert.Contains(plan.Notes, n => n.StartsWith("Legacy: 1 DLL reference(s) left as they are, because the project uses packages.config"));
    }

    [Fact]
    public void Legacy_project_without_packages_config_gets_a_versioned_PackageReference()
    {
        var dir = Solution(("Legacy", false, Group(Ref("Acme.Logging"))));

        FileChange.ApplyAll(FileReferenceConversion.Plan(SolutionLoader.Load(dir), PackageMap.Empty, byName: true).Changes);

        Assert.Contains("<PackageReference Include=\"Acme.Logging\" Version=\"2.3.0\" />", Csproj(dir, "Legacy"));
    }

    [Fact]
    public void Template_lists_every_dll_once_with_a_guess()
    {
        var dir = Solution(("A", true, Group(Ref("Acme.Logging"), Ref("Vendor.Sdk", "5.0.1.2"))), ("B", true, Group(Ref("Acme.Logging"))));
        var references = SolutionLoader.Load(dir).Projects.SelectMany(p => p.FileReferences);

        var json = PackageMap.Template(references);
        File.WriteAllText(Path.Combine(dir, PackageMap.FileName), json);
        var map = PackageMap.Load(Path.Combine(dir, PackageMap.FileName)); // what we write must load

        Assert.Equal(new PackageTarget.Package("Acme.Logging", "2.3.0"), map.Resolve("Acme.Logging"));
        Assert.Equal(new PackageTarget.Package("Vendor.Sdk", "5.0.1.2"), map.Resolve("Vendor.Sdk"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"Acme.Logging\":"));
    }

    [Fact]
    public void Analyze_reports_file_references_and_fixed_versions()
    {
        var dir = Solution(("A", true,
            "  <PropertyGroup>\n    <Version>1.0.0</Version>\n  </PropertyGroup>\n" + Group(Ref("Acme.Logging"),
            "    <PackageReference Include=\"Foo\">\n      <Version>1.0.0</Version>\n    </PackageReference>")),
            ("B", true, Group(Ref("Acme.Core"))));
        File.WriteAllText(Path.Combine(dir, "B", "B.nuspec"), "<package><metadata><id>B</id><version>1.0.0</version></metadata></package>");

        var findings = new SolutionAnalyzer().Analyze(dir).Findings;

        Assert.Contains(findings, f => f.Project == "A" && f.Message.StartsWith("1 DLL reference(s) by file path outside packages/: Acme.Logging"));
        Assert.Contains(findings, f => f.Project == "A" && f.Message.StartsWith("Version fixed at 1.0.0 (<Version> in A.csproj)"));
        Assert.Contains(findings, f => f.Project == "B" && f.Message.StartsWith("Version fixed at 1.0.0 (B.nuspec)") && f.File == "B.nuspec");
    }

    [Fact]
    public async Task Refs_command_init_map_then_convert_and_commit()
    {
        var dir = Solution(("A", true, Group(Ref("Acme.Logging"))));
        Git.InitWithCommit(dir);

        var init = new StringWriter();
        await new App(new StringReader(""), init, interactive: false).RunAsync(["refs", dir, "--init-map"]);
        Assert.Contains("Wrote dotnet-lift.packages.json with 1 DLL(s).", init.ToString());
        Git.Run(dir, "add", "-A");
        Git.Run(dir, "commit", "-q", "-m", "map");

        var output = new StringWriter();
        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["refs", dir, "--yes"]);

        Assert.Equal(0, exit);
        Assert.Contains("✓ A: Acme.Logging → Acme.Logging 2.3.0", output.ToString());
        Assert.Contains("dotnet-lift: Reference DLLs as NuGet packages", Git.LogSubjects(dir));
        Assert.Equal("", Git.Status(dir));
    }
}
