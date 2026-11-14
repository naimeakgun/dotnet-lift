using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class TransformTests
{
    private static readonly UpgradeStep AnyStep =
        new(1, "x", "x", "x", "x", StepMode.Automatic, Risk.Low, Array.Empty<Finding>());

    private static ProjectInfo Load(string dir, string name) =>
        ProjectLoader.Load(Path.Combine(dir, name, name + ".csproj"));

    [Fact]
    public void PackageReference_migration_moves_packages_and_deletes_packages_config()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var core = Load(dir, "LegacyShop.Core");

        var changes = new PackageReferenceMigration().Prepare(core, AnyStep, "net10.0");

        Assert.Equal(2, changes.Count);
        var csproj = changes[0].NewContent!;
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", csproj);
        Assert.DoesNotContain("HintPath", csproj);
        Assert.DoesNotContain("packages.config", csproj);
        Assert.Contains("<Compile Include=\"Services\\PriceCalculator.cs\" />", csproj); // untouched otherwise
        Assert.True(changes[1].IsDelete);
        Assert.EndsWith("packages.config", changes[1].Path);
    }

    [Fact]
    public void Migrated_legacy_project_still_loads_with_its_packages()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var data = Load(dir, "LegacyShop.Data");

        FileChange.ApplyAll(new PackageReferenceMigration().Prepare(data, AnyStep, "net10.0"));
        var reloaded = Load(dir, "LegacyShop.Data");

        Assert.False(reloaded.IsSdkStyle);
        Assert.False(reloaded.HasPackagesConfig);
        Assert.Contains(reloaded.Packages, p => p.Id == "EntityFramework" && p.Version == "6.4.4");
        Assert.DoesNotContain("EntityFramework", reloaded.AssemblyReferences);
        Assert.Contains("System.Web", reloaded.AssemblyReferences);
    }

    [Fact]
    public void Sdk_conversion_keeps_references_packages_and_project_references()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var data = Load(dir, "LegacyShop.Data");

        var changes = new SdkStyleConversion().Prepare(data, AnyStep, "net10.0");
        var csproj = changes[0].NewContent!;

        Assert.StartsWith("<Project Sdk=\"Microsoft.NET.Sdk\">", csproj);
        Assert.Contains("<TargetFramework>net48</TargetFramework>", csproj);
        Assert.Contains("<GenerateAssemblyInfo>false</GenerateAssemblyInfo>", csproj);
        Assert.Contains("<Reference Include=\"System.Web\" />", csproj);
        Assert.Contains("<Reference Include=\"System.ComponentModel.DataAnnotations\" />", csproj);
        Assert.Contains("<Reference Include=\"System.Configuration\" />", csproj);
        Assert.DoesNotContain("\"System.Core\"", csproj);
        Assert.Contains("<PackageReference Include=\"EntityFramework\" Version=\"6.4.4\" />", csproj);
        Assert.Contains("<ProjectReference Include=\"..\\LegacyShop.Core\\LegacyShop.Core.csproj\" />", csproj);
        Assert.DoesNotContain("<Compile", csproj);
        Assert.DoesNotContain("<RootNamespace>", csproj); // same as the project name
        Assert.Contains(changes, c => c.IsDelete && c.Path.EndsWith("packages.config"));
    }

    [Fact]
    public void Converted_project_loads_as_sdk_style()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var core = Load(dir, "LegacyShop.Core");

        FileChange.ApplyAll(new SdkStyleConversion().Prepare(core, AnyStep, "net10.0"));
        var reloaded = Load(dir, "LegacyShop.Core");

        Assert.True(reloaded.IsSdkStyle);
        Assert.Equal("net48", reloaded.TargetFramework);
        Assert.False(reloaded.HasPackagesConfig);
        Assert.Contains(reloaded.Packages, p => p.Id == "Newtonsoft.Json");
    }

    [Fact]
    public void Files_the_old_project_did_not_compile_are_excluded()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var stray = Path.Combine(dir, "LegacyShop.Core", "Old", "Unused.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "this file was never part of the build");

        var csproj = new SdkStyleConversion().Prepare(Load(dir, "LegacyShop.Core"), AnyStep, "net10.0")[0].NewContent!;

        Assert.Contains("<Compile Remove=\"Old\\Unused.cs\" />", csproj);
        Assert.DoesNotContain("PriceCalculator", csproj);
    }

    [Fact]
    public void Web_projects_are_not_converted_by_the_library_conversion()
    {
        var web = ProjectLoader.Load(Path.Combine(Samples.LegacyShopDir, "LegacyShop.Web", "LegacyShop.Web.csproj"));

        Assert.False(new SdkStyleConversion().CanExecute(web));
    }

    [Theory]
    [InlineData("v4.8", "net48")]
    [InlineData("v4.7.2", "net472")]
    [InlineData("v4.6.1", "net461")]
    [InlineData(null, "net48")]
    [InlineData("garbage", "net48")]
    public void Maps_target_framework_versions(string? input, string expected)
    {
        Assert.Equal(expected, SdkStyleConversion.ToTfm(input));
    }

    [Fact]
    public void Line_diff_counts_and_renders_changes()
    {
        var stats = LineDiff.Count("a\nb\nc\n", "a\nx\nc\n");
        var diff = LineDiff.Unified("a\nb\nc\n", "a\nx\nc\n", "file.txt");

        Assert.Equal(1, stats.Added);
        Assert.Equal(1, stats.Removed);
        Assert.Contains("-b", diff);
        Assert.Contains("+x", diff);
        Assert.Contains(" a", diff);
    }
}
