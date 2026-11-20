using DotnetLift.Cli;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class StaticFilesTests
{
    private static readonly UpgradeStep AnyStep =
        new(1, "x", "static-files", "x", "x", StepMode.Automatic, Risk.Low, Array.Empty<Finding>());

    /// <summary>Bytes that are not valid UTF-8: reading and writing them as text would change them.</summary>
    private static readonly byte[] IconBytes = [0x00, 0x00, 0x01, 0x00, 0xFF, 0xFE, 0xC3, 0x28, 0x80, 0x0D, 0x0A, 0x1A];

    private const string AspNetCoreProject = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <Content Include="Content\site.css" />
            <None Include="Scripts/site.js">
              <CopyToPublishDirectory>Always</CopyToPublishDirectory>
            </None>
            <Content Include="Web.config" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\LegacyShop.Core\LegacyShop.Core.csproj" />
            <ProjectReference Include="..\LegacyShop.Data\LegacyShop.Data.csproj" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>The sample with its web project already switched to Microsoft.NET.Sdk.Web (what the ASP.NET Core project step will do).</summary>
    private static string SampleWithAspNetCoreWebProject()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var web = Path.Combine(dir, "LegacyShop.Web");
        File.WriteAllText(Path.Combine(web, "LegacyShop.Web.csproj"), AspNetCoreProject);
        File.WriteAllBytes(Path.Combine(web, "favicon.ico"), IconBytes);
        Directory.CreateDirectory(Path.Combine(web, "fonts"));
        File.WriteAllBytes(Path.Combine(web, "fonts", "shop.woff2"), IconBytes);
        File.WriteAllText(Path.Combine(web, "Content", "Web.config"), "<configuration />");
        return dir;
    }

    private static ProjectInfo Web(string dir) =>
        ProjectLoader.Load(Path.Combine(dir, "LegacyShop.Web", "LegacyShop.Web.csproj"));

    [Fact]
    public void Waits_while_the_web_project_is_still_a_dotnet_framework_project()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var web = Web(dir);
        var executor = new StaticFilesToWwwroot();

        Assert.True(executor.CanExecute(web));
        Assert.Contains("ASP.NET Core project step", executor.NotReadyReason(web));
        Assert.Empty(executor.Prepare(web, AnyStep, "net10.0"));
        Assert.True(File.Exists(Path.Combine(dir, "LegacyShop.Web", "Content", "site.css")));
    }

    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>", true)]
    [InlineData("<Project Sdk=\"microsoft.net.sdk.web/10.0.0\"></Project>", true)]
    [InlineData("<Project><Sdk Name=\"Microsoft.NET.Sdk.Web\" /></Project>", true)]
    [InlineData("<Project><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk.Web\" /></Project>", true)]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", false)]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk.Razor\"></Project>", false)]
    [InlineData("<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"></Project>", false)]
    [InlineData("not xml", false)]
    public void Recognizes_ASP_NET_Core_project_files(string xml, bool expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "App.csproj");
        File.WriteAllText(path, xml);

        Assert.Equal(expected, StaticFilesToWwwroot.IsAspNetCoreProject(path));
    }

    [Fact]
    public void Plans_moves_that_keep_folders_so_urls_stay_the_same()
    {
        var dir = SampleWithAspNetCoreWebProject();
        var web = Path.Combine(dir, "LegacyShop.Web");

        var (moves, conflicts) = StaticFilesToWwwroot.PlanMoves(web);

        string Rel(string p) => Path.GetRelativePath(web, p).Replace('\\', '/');
        Assert.Equal(
            new[]
            {
                ("Content/site.css", "wwwroot/Content/site.css"),
                ("favicon.ico", "wwwroot/favicon.ico"),
                ("fonts/shop.woff2", "wwwroot/fonts/shop.woff2"),
                ("Scripts/site.js", "wwwroot/Scripts/site.js"),
            }.OrderBy(m => m.Item1, StringComparer.Ordinal),
            moves.Select(m => (Rel(m.MovedFrom!), Rel(m.Path))).OrderBy(m => m.Item1, StringComparer.Ordinal));
        Assert.All(moves, m => Assert.True(m.IsMove));
        Assert.Empty(conflicts);
        // Views, controllers and configuration are not static files.
        Assert.DoesNotContain(moves, m => m.MovedFrom!.EndsWith(".config") || m.MovedFrom!.Contains("Views"));
    }

    [Fact]
    public void A_file_that_already_exists_in_wwwroot_is_left_where_it_is()
    {
        var dir = SampleWithAspNetCoreWebProject();
        var web = Path.Combine(dir, "LegacyShop.Web");
        Directory.CreateDirectory(Path.Combine(web, "wwwroot", "Content"));
        File.WriteAllText(Path.Combine(web, "wwwroot", "Content", "site.css"), "/* newer */");

        var (moves, conflicts) = StaticFilesToWwwroot.PlanMoves(web);

        Assert.Equal(Path.Combine(web, "Content", "site.css"), Assert.Single(conflicts));
        Assert.DoesNotContain(moves, m => m.MovedFrom == Path.Combine(web, "Content", "site.css"));
        Assert.Contains(moves, m => m.MovedFrom == Path.Combine(web, "Scripts", "site.js"));
    }

    [Fact]
    public void Moves_binary_files_unchanged_and_removes_emptied_folders()
    {
        var dir = SampleWithAspNetCoreWebProject();
        var web = Path.Combine(dir, "LegacyShop.Web");

        FileChange.ApplyAll(new StaticFilesToWwwroot().Prepare(Web(dir), AnyStep, "net10.0"));

        Assert.Equal(IconBytes, File.ReadAllBytes(Path.Combine(web, "wwwroot", "favicon.ico")));
        Assert.Equal(IconBytes, File.ReadAllBytes(Path.Combine(web, "wwwroot", "fonts", "shop.woff2")));
        Assert.True(File.Exists(Path.Combine(web, "wwwroot", "Content", "site.css")));
        Assert.True(File.Exists(Path.Combine(web, "wwwroot", "Scripts", "site.js")));
        Assert.False(File.Exists(Path.Combine(web, "favicon.ico")));
        Assert.False(Directory.Exists(Path.Combine(web, "Scripts")));
        Assert.False(Directory.Exists(Path.Combine(web, "fonts")));
        // Content/ keeps its Web.config, which is configuration, not a static file.
        Assert.True(File.Exists(Path.Combine(web, "Content", "Web.config")));
    }

    [Fact]
    public void Explicit_project_items_follow_the_moved_files()
    {
        var dir = SampleWithAspNetCoreWebProject();

        var changes = new StaticFilesToWwwroot().Prepare(Web(dir), AnyStep, "net10.0");
        var csproj = Assert.Single(changes, c => !c.IsMove).NewContent!;

        // Sdk.Web includes wwwroot/** as Content already: a plain item would be a duplicate.
        Assert.DoesNotContain("site.css", csproj);
        // An item with metadata keeps it, as an update of the default Content item.
        Assert.Contains("<Content Update=\"wwwroot\\Scripts\\site.js\">", csproj);
        Assert.Contains("<CopyToPublishDirectory>Always</CopyToPublishDirectory>", csproj);
        Assert.DoesNotContain("<None", csproj);
        Assert.Contains("<Content Include=\"Web.config\" />", csproj);
        Assert.Contains("<ProjectReference Include=\"..\\LegacyShop.Core\\LegacyShop.Core.csproj\" />", csproj);
    }

    [Fact]
    public void Project_file_is_unchanged_when_it_lists_no_moved_file()
    {
        var dir = SampleWithAspNetCoreWebProject();
        File.WriteAllText(Path.Combine(dir, "LegacyShop.Web", "LegacyShop.Web.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var changes = new StaticFilesToWwwroot().Prepare(Web(dir), AnyStep, "net10.0");

        Assert.All(changes, c => Assert.True(c.IsMove));
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void Git_commit_records_moves_as_renames_and_leaves_out_ignored_files()
    {
        var dir = SampleWithAspNetCoreWebProject();
        var web = Path.Combine(dir, "LegacyShop.Web");
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "*.min.css" + Environment.NewLine);
        File.WriteAllText(Path.Combine(web, "Content", "site.min.css"), "body{}");
        Git.InitWithCommit(dir);
        var git = GitRepository.Find(dir)!;

        var changes = new StaticFilesToWwwroot().Prepare(Web(dir), AnyStep, "net10.0");
        FileChange.ApplyAll(changes);
        var committed = git.Commit(changes.SelectMany(c => c.Paths), "move static files");

        Assert.True(committed);
        Assert.Equal("", Git.Status(dir));
        var show = Git.Run(dir, "show", "-M", "--name-status", "--format=", "HEAD");
        Assert.Contains("LegacyShop.Web/Content/site.css\tLegacyShop.Web/wwwroot/Content/site.css", show);
        Assert.Contains("LegacyShop.Web/favicon.ico\tLegacyShop.Web/wwwroot/favicon.ico", show);
        Assert.DoesNotContain("site.min.css", show);
        Assert.True(File.Exists(Path.Combine(web, "wwwroot", "Content", "site.min.css"))); // moved, just not in git
    }

    [Fact]
    public void Git_commit_reports_when_every_file_is_ignored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "*.log" + Environment.NewLine);
        Git.InitWithCommit(dir);
        File.WriteAllText(Path.Combine(dir, "a.log"), "x");
        var move = FileChange.Move(Path.Combine(dir, "a.log"), Path.Combine(dir, "logs", "a.log"));
        move.Apply();

        Assert.False(GitRepository.Find(dir)!.Commit(move.Paths, "nothing to stage"));
        Assert.Single(Git.LogSubjects(dir));
    }

    [Fact]
    public async Task Upgrade_waits_for_the_ASP_NET_Core_project_on_the_legacy_sample()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        Git.InitWithCommit(dir);

        var (exit, output) = await Run(dir);

        Assert.Equal(0, exit);
        Assert.Contains("→ Not yet: the project is still an ASP.NET (.NET Framework) project", output);
        Assert.Contains("waiting for an earlier step 1.", output);
        Assert.True(File.Exists(Path.Combine(dir, "LegacyShop.Web", "Content", "site.css")));
        Assert.False(Directory.Exists(Path.Combine(dir, "LegacyShop.Web", "wwwroot")));
    }

    [Fact]
    public async Task Upgrade_moves_static_files_of_an_ASP_NET_Core_project_in_one_commit()
    {
        var dir = SampleWithAspNetCoreWebProject();
        Git.InitWithCommit(dir);

        var (exit, output) = await Run(dir);

        Assert.Equal(0, exit);
        Assert.Contains("> LegacyShop.Web/Content/site.css → LegacyShop.Web/wwwroot/Content/site.css (move)", output);
        Assert.Contains("dotnet-lift: Move static files to wwwroot (LegacyShop.Web)", Git.LogSubjects(dir));
        Assert.Equal("", Git.Status(dir));
        var show = Git.Run(dir, "show", "-M", "--name-status", "--format=", "HEAD");
        Assert.Contains("LegacyShop.Web/Scripts/site.js\tLegacyShop.Web/wwwroot/Scripts/site.js", show);
        Assert.Contains("LegacyShop.Web/LegacyShop.Web.csproj", show);
    }

    private static async Task<(int Exit, string Output)> Run(string dir)
    {
        var output = new StringWriter();
        var exit = await new App(new StringReader(""), output, interactive: true, new FakeBuilder())
            .RunAsync(["upgrade", dir, "--yes"]);
        return (exit, output.ToString());
    }
}
