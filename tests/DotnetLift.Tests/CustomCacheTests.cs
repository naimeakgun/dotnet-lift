using DotnetLift.Cli;
using DotnetLift.Core;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Tests;

public class CustomCacheTests
{
    private const string Templates = """
        {
          "cache": {
            "get": "CacheManager.Get<object>({key})",
            "set": "CacheManager.Set({key}, {value})",
            "setAbsolute": "CacheManager.Set({key}, {value}, {absolute})",
            "setSliding": "CacheManager.SetSliding({key}, {value}, {sliding})",
            "remove": "CacheManager.Remove({key})",
            "usings": ["Acme.Caching"]
          }
        }
        """;

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CacheMap Map(string json = Templates)
    {
        var path = Path.Combine(TempDir(), MigrationProfile.FileName);
        File.WriteAllText(path, json);
        return MigrationProfile.Load(path).Cache!;
    }

    [Fact]
    public void Every_call_shape_with_a_template_is_rewritten()
    {
        const string source = """
            using System;
            using System.Web;
            using System.Web.Caching;

            class C
            {
                void M(string key, object value, TimeSpan idle)
                {
                    var a = HttpRuntime.Cache[key];
                    HttpRuntime.Cache[key] = value;
                    var b = HttpRuntime.Cache.Get(key);
                    HttpRuntime.Cache.Insert(key, value);
                    HttpRuntime.Cache.Insert(key, value, null, DateTime.UtcNow.AddMinutes(5), Cache.NoSlidingExpiration);
                    HttpContext.Current.Cache.Insert(key, value, null, Cache.NoAbsoluteExpiration, idle);
                    HttpRuntime.Cache.Remove(key);
                }
            }
            """;

        var result = CustomCacheRewrite.Rewrite(source, Map())!;
        var text = result.NewText.Replace("\r\n", "\n");

        Assert.Equal(7, result.Replaced);
        Assert.Equal(0, result.LeftAlone);
        Assert.Contains("var a = CacheManager.Get<object>(key);", text);
        Assert.Contains("        CacheManager.Set(key, value);\n        var b", text);
        Assert.Contains("var b = CacheManager.Get<object>(key);", text);
        Assert.Contains("CacheManager.Set(key, value, DateTime.UtcNow.AddMinutes(5));", text);
        Assert.Contains("CacheManager.SetSliding(key, value, idle);", text);
        Assert.Contains("CacheManager.Remove(key);", text);
        Assert.Contains("using System.Web;\nusing Acme.Caching;\n", text); // in place of System.Web.Caching
        Assert.DoesNotContain("System.Web.Caching", text);
        Assert.DoesNotContain("HttpRuntime", text);
    }

    [Fact]
    public void Shapes_without_a_template_and_dependencies_stay_and_are_counted()
    {
        const string source = """
            using System.Web;
            using System.Web.Caching;
            class C
            {
                void M(string key, object value, CacheDependency dependency)
                {
                    HttpRuntime.Cache.Insert(key, value, dependency);
                    HttpRuntime.Cache.Remove(key);
                    var n = HttpRuntime.Cache.Count;
                }
            }
            """;

        var result = CustomCacheRewrite.Rewrite(source, Map("""{ "cache": { "get": "CacheManager.Get<object>({key})" } }"""))!;

        Assert.Equal(0, result.Replaced);
        Assert.Equal(3, result.LeftAlone);
        Assert.Equal(source, result.NewText);
    }

    [Theory]
    [InlineData("""{ "cache": { "fetch": "X.Get({key})" } }""", "unknown setting cache.fetch")]
    [InlineData("""{ "cache": { "get": "X.Get({key}, {value})" } }""", "cache.get uses {value}; it can use {key}")]
    [InlineData("""{ "cache": { "remove": "X.Clear()" } }""", "cache.remove must use {key}")]
    [InlineData("""{ "cache": { "get": "X.Get({key}" } }""", "cache.get must be a C# expression")]
    [InlineData("""{ "cache": { "usings": ["A"] } }""", "needs at least one template")]
    public void Broken_templates_stop_with_a_clear_error(string json, string message)
    {
        var path = Path.Combine(TempDir(), MigrationProfile.FileName);
        File.WriteAllText(path, json);

        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => MigrationProfile.Load(path)).Message);
    }

    [Fact]
    public async Task Upgrade_with_templates_uses_the_team_cache_and_adds_no_caching_package()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        File.WriteAllText(Path.Combine(dir, MigrationProfile.FileName), Templates.Replace("{\n  \"cache\"", "{\n  \"target\": \"net10.0\",\n  \"cache\""));
        Git.InitWithCommit(dir);

        var step = Assert.Single(UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir), MigrationProfile.Find(dir)).Steps,
            s => s.Id == "api-rewrites" && s.Project == "LegacyShop.Data");
        Assert.Equal("Move HttpRuntime.Cache to the team's cache API (Roslyn)", step.Title);

        await new App(new StringReader(""), new StringWriter(), interactive: false, new FakeBuilder()).RunAsync(["upgrade", dir, "--yes"]);

        var repository = File.ReadAllText(Path.Combine(dir, "LegacyShop.Data", "Repositories", "ProductRepository.cs"));
        Assert.Contains("var cached = CacheManager.Get<object>(AllProductsKey) as IList<Product>;", repository);
        Assert.Contains("CacheManager.Set(AllProductsKey, products,", repository);
        Assert.Contains("using Acme.Caching;", repository);
        Assert.DoesNotContain("HttpRuntime", repository);
        Assert.DoesNotContain("System.Runtime.Caching", File.ReadAllText(Path.Combine(dir, "LegacyShop.Data", "LegacyShop.Data.csproj")));
    }
}
