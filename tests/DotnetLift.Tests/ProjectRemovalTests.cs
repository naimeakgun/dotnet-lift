using DotnetLift.Cli;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class ProjectRemovalTests
{
    private const string Guid1 = "{11111111-0000-0000-0000-000000000001}";
    private const string Guid2 = "{11111111-0000-0000-0000-000000000002}";
    private const string Guid3 = "{11111111-0000-0000-0000-000000000003}";
    private const string FolderGuid = "{22222222-0000-0000-0000-000000000001}";

    /// <summary>
    /// A solution "S.sln" (CRLF line endings, like Visual Studio writes it) with projects A, B and C, a solution
    /// folder holding A, and B with a build-order entry on A. The project files get the given item XML.
    /// </summary>
    private static string Solution(string aItems = "", string bItems = "", string cItems = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        foreach (var (name, items) in new[] { ("A", aItems), ("B", bItems), ("C", cItems) })
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
            File.WriteAllText(Path.Combine(dir, name, name + ".csproj"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
                "  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n    <OutputType>Library</OutputType>\n  </PropertyGroup>\n" +
                $"  <ItemGroup>\n    <Reference Include=\"System\" />\n{items}\n  </ItemGroup>\n</Project>\n");
        }

        const string cs = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
        string[] lines =
        [
            "",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17",
            $"Project(\"{cs}\") = \"A\", \"A\\A.csproj\", \"{Guid1}\"",
            "EndProject",
            $"Project(\"{cs}\") = \"B\", \"B\\B.csproj\", \"{Guid2}\"",
            "\tProjectSection(ProjectDependencies) = postProject",
            $"\t\t{Guid1} = {Guid1}",
            "\tEndProjectSection",
            "EndProject",
            $"Project(\"{cs}\") = \"C\", \"C\\C.csproj\", \"{Guid3}\"",
            "EndProject",
            $"Project(\"{{2150E333-8FDC-42A3-9474-1A3956D46DE8}}\") = \"Old\", \"Old\", \"{FolderGuid}\"",
            "EndProject",
            "Global",
            "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
            .. new[] { Guid1, Guid2, Guid3 }.SelectMany(g => new[]
            {
                $"\t\t{g}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                $"\t\t{g}.Debug|Any CPU.Build.0 = Debug|Any CPU",
            }),
            "\tEndGlobalSection",
            "\tGlobalSection(NestedProjects) = preSolution",
            $"\t\t{Guid1} = {FolderGuid}",
            "\tEndGlobalSection",
            "EndGlobal",
            "",
        ];
        File.WriteAllText(Path.Combine(dir, "S.sln"), string.Join("\r\n", lines));
        return dir;
    }

    private static string ProjectRef(string name) =>
        $"    <ProjectReference Include=\"..\\{name}\\{name}.csproj\">\n      <Name>{name}</Name>\n    </ProjectReference>";

    private static string Sln(string dir) => File.ReadAllText(Path.Combine(dir, "S.sln"));

    [Fact]
    public void A_project_nothing_uses_leaves_the_solution_with_all_its_lines()
    {
        var dir = Solution(bItems: ProjectRef("C"));

        var plan = ProjectRemoval.Plan(SolutionLoader.Load(Path.Combine(dir, "S.sln")), "a"); // name match ignores case
        Assert.True(plan.CanRemove);
        Assert.Empty(plan.Dependents);
        Assert.Equal(new[] { "B" }, plan.BuildOrderEntries);
        plan.SolutionChange!.Apply();

        var sln = Sln(dir);
        Assert.DoesNotContain(Guid1, sln);
        Assert.DoesNotContain("\"A\\A.csproj\"", sln);
        Assert.Contains($"{Guid2}.Debug|Any CPU.Build.0", sln);
        Assert.Contains("\"C\\C.csproj\"", sln);
        Assert.Contains("\tProjectSection(ProjectDependencies) = postProject\r\n\tEndProjectSection\r\n", sln);
        Assert.DoesNotContain("\n\n", sln.Replace("\r\n", "\n").TrimStart('\n'));
        Assert.Equal(sln.Split('\n').Length - 1, sln.Split("\r\n").Length - 1); // every line still ends in CRLF
        Assert.True(File.Exists(Path.Combine(dir, "A", "A.csproj"))); // the folder stays

        var reloaded = SolutionLoader.Load(Path.Combine(dir, "S.sln"));
        Assert.Equal(new[] { "B", "C" }, reloaded.Projects.Select(p => p.Name));
    }

    [Fact]
    public void Direct_and_indirect_dependents_are_listed_and_block_the_removal()
    {
        var dir = Solution(bItems: ProjectRef("A"), cItems: ProjectRef("B"));
        var before = Sln(dir);

        var plan = ProjectRemoval.Plan(SolutionLoader.Load(dir), "A");

        Assert.False(plan.CanRemove);
        Assert.Null(plan.SolutionChange);
        Assert.Equal(
            new[] { new ProjectDependent("B", "project reference", true), new ProjectDependent("C", "through B", false) },
            plan.Dependents);
        Assert.Equal(before, Sln(dir));
    }

    [Fact]
    public void A_file_reference_to_the_projects_dll_counts_as_a_dependent()
    {
        var dir = Solution(
            aItems: "",
            cItems: "    <Reference Include=\"A, Version=1.0.0.0, Culture=neutral\">\n      <HintPath>..\\SolutionItems\\A.dll</HintPath>\n    </Reference>");

        var plan = ProjectRemoval.Plan(SolutionLoader.Load(dir), "A");

        Assert.Equal(new[] { new ProjectDependent("C", "file reference to A.dll", true) }, plan.Dependents);
        Assert.False(plan.CanRemove);
    }

    [Fact]
    public void The_assembly_name_in_the_project_file_is_what_file_references_point_to()
    {
        var dir = Solution(cItems: "    <Reference Include=\"Shared.Legacy\">\n      <HintPath>..\\lib\\Shared.Legacy.dll</HintPath>\n    </Reference>");
        var a = Path.Combine(dir, "A", "A.csproj");
        File.WriteAllText(a, File.ReadAllText(a).Replace("<OutputType>Library</OutputType>", "<OutputType>Library</OutputType>\n    <AssemblyName>Shared.Legacy</AssemblyName>"));

        var plan = ProjectRemoval.Plan(SolutionLoader.Load(dir), "A");

        Assert.Equal("C", Assert.Single(plan.Dependents).Project);
    }

    [Fact]
    public void An_unknown_project_names_the_ones_there_are()
    {
        var dir = Solution();

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectRemoval.Plan(SolutionLoader.Load(dir), "Nope"));

        Assert.Contains("Projects: A, B, C", ex.Message);
    }

    [Fact]
    public async Task Cli_removes_the_project_in_one_commit()
    {
        var dir = Solution();
        Git.InitWithCommit(dir);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["cleanup", dir, "--remove-project", "A", "--yes"]);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("Nothing in the solution depends on A.", text);
        Assert.Contains("ℹ B: its build-order entry for A in the solution goes too.", text);
        Assert.Contains("The folder A/ stays on disk", text);
        Assert.Equal(new[] { "dotnet-lift: Remove A from the solution", "initial" }, Git.LogSubjects(dir));
        Assert.Equal("", Git.Status(dir));
        Assert.DoesNotContain(Guid1, Sln(dir));
    }

    [Fact]
    public async Task Cli_refuses_while_something_depends_on_it()
    {
        var dir = Solution(bItems: ProjectRef("A"), cItems: ProjectRef("B"));
        var before = Sln(dir);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["cleanup", dir, "--remove-project", "A", "--yes"]);

        Assert.Equal(1, exit);
        var text = output.ToString();
        Assert.Contains("✗ B (project reference)", text);
        Assert.Contains("• C (through B)", text);
        Assert.Contains("A stays: remove the references marked ✗ first", text);
        Assert.Equal(before, Sln(dir));
    }

    [Fact]
    public async Task Dry_run_shows_the_solution_diff_and_writes_nothing()
    {
        var dir = Solution();
        var before = Sln(dir);
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false).RunAsync(["cleanup", dir, "--remove-project", "A", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.Contains("-Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"A\"", output.ToString());
        Assert.Contains("Dry run: nothing was written.", output.ToString());
        Assert.Equal(before, Sln(dir));
    }

    [Fact]
    public async Task A_single_project_is_not_a_solution()
    {
        var dir = Solution();
        var output = new StringWriter();

        var exit = await new App(new StringReader(""), output, interactive: false)
            .RunAsync(["cleanup", Path.Combine(dir, "A", "A.csproj"), "--remove-project", "A"]);

        Assert.Equal(1, exit);
        Assert.Contains("needs a .sln file", output.ToString());
    }

    [Fact]
    public void Sample_core_library_cannot_go_because_web_and_data_use_it()
    {
        var plan = ProjectRemoval.Plan(SolutionLoader.Load(Samples.LegacyShopDir), "LegacyShop.Core");

        Assert.False(plan.CanRemove);
        Assert.Equal(new[] { "LegacyShop.Data", "LegacyShop.Web" }, plan.Dependents.Where(d => d.Direct).Select(d => d.Project).Order());
    }
}
