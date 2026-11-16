using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class RewriteTests
{
    private static string Wrap(string body, string usings = "using System;\nusing System.Web;\nusing System.Web.Caching;\n") =>
        usings + "\nclass C\n{\n    void M(string k, object v)\n    {\n" + body + "\n    }\n}\n";

    [Fact]
    public void Rewrites_the_sample_repository()
    {
        var source = File.ReadAllText(Path.Combine(Samples.LegacyShopDir, "LegacyShop.Data", "Repositories", "ProductRepository.cs"));

        var result = HttpRuntimeCacheRewrite.Rewrite(source)!;

        Assert.Equal(2, result.Replaced);
        Assert.Equal(0, result.LeftAlone);
        Assert.Contains("var cached = MemoryCache.Default[AllProductsKey] as IList<Product>;", result.NewText);
        Assert.Contains("MemoryCache.Default.Set(AllProductsKey, products, DateTime.UtcNow.AddMinutes(5));", result.NewText);
        Assert.Contains("using System.Runtime.Caching;", result.NewText);
        Assert.DoesNotContain("System.Web.Caching", result.NewText);
        Assert.Contains("using System.Web;", result.NewText); // HttpContext.Current is still used
        Assert.Contains("HttpContext.Current.User.Identity.Name", result.NewText);
    }

    [Fact]
    public void Strings_comments_and_other_types_are_not_touched()
    {
        const string source = """
            using System;

            class C
            {
                // HttpRuntime.Cache was used here once
                string s = "HttpRuntime.Cache[\"x\"]";
                object o = MyApp.HttpRuntime.Cache["x"];
                object p = HttpRuntime.Cache["x"]; // no `using System.Web;`: this HttpRuntime is not System.Web's
            }
            """;

        Assert.Null(HttpRuntimeCacheRewrite.Rewrite(source));
    }

    [Fact]
    public void Fully_qualified_usage_is_rewritten_without_a_using()
    {
        var result = HttpRuntimeCacheRewrite.Rewrite(Wrap("        var x = System.Web.HttpRuntime.Cache[k];", usings: "using System;\n"))!;

        Assert.Equal(1, result.Replaced);
        Assert.Contains("var x = System.Runtime.Caching.MemoryCache.Default[k];", result.NewText);
    }

    [Fact]
    public void Indexer_writes_get_remove_and_HttpContext_Current_Cache_are_rewritten()
    {
        var result = HttpRuntimeCacheRewrite.Rewrite(Wrap("""
                    HttpContext.Current.Cache[k] = v;
                    var a = HttpRuntime.Cache.Get(k);
                    HttpRuntime.Cache.Remove(k);
                    HttpRuntime.Cache.Insert(k, v);
            """))!;

        Assert.Equal(4, result.Replaced);
        Assert.Contains("MemoryCache.Default[k] = v;", result.NewText);
        Assert.Contains("var a = MemoryCache.Default.Get(k);", result.NewText);
        Assert.Contains("MemoryCache.Default.Remove(k);", result.NewText);
        Assert.Contains("MemoryCache.Default.Set(k, v, ObjectCache.InfiniteAbsoluteExpiration);", result.NewText);
    }

    [Fact]
    public void Sliding_expiration_becomes_a_cache_item_policy()
    {
        var result = HttpRuntimeCacheRewrite.Rewrite(Wrap(
            "        HttpRuntime.Cache.Insert(k, v, null, Cache.NoAbsoluteExpiration, TimeSpan.FromMinutes(10));"))!;

        Assert.Contains("Set(k, v, new CacheItemPolicy { SlidingExpiration = TimeSpan.FromMinutes(10) });", result.NewText);
    }

    [Fact]
    public void Calls_without_an_exact_equivalent_are_left_alone_and_System_Web_Caching_stays()
    {
        var result = HttpRuntimeCacheRewrite.Rewrite(Wrap("""
                    CacheDependency dep = null;
                    HttpRuntime.Cache.Insert(k, v, dep, DateTime.UtcNow.AddMinutes(1), Cache.NoSlidingExpiration);
                    var n = HttpRuntime.Cache.Count;
                    var x = HttpRuntime.Cache[k];
            """))!;

        Assert.Equal(1, result.Replaced);
        Assert.Equal(2, result.LeftAlone);
        Assert.Contains("HttpRuntime.Cache.Insert(k, v, dep,", result.NewText);
        Assert.Contains("using System.Web.Caching;", result.NewText);
        // System.Web.Caching is still needed, so the new code is fully qualified to avoid ambiguous names.
        Assert.Contains("var x = System.Runtime.Caching.MemoryCache.Default[k];", result.NewText);
    }

    [Fact]
    public void Step_rewrites_code_and_adds_System_Runtime_Caching_for_each_target()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var path = Path.Combine(dir, "LegacyShop.Data", "LegacyShop.Data.csproj");
        var step = new UpgradeStep(1, "LegacyShop.Data", "x", "", "", StepMode.Automatic, Risk.Low, [], "net48;net10.0");
        FileChange.ApplyAll(new PackageReferenceMigration().Prepare(ProjectLoader.Load(path), step, "net10.0"));
        FileChange.ApplyAll(new SdkStyleConversion().Prepare(ProjectLoader.Load(path), step, "net10.0"));
        FileChange.ApplyAll(new Retarget().Prepare(ProjectLoader.Load(path), step, "net10.0"));

        var changes = new ApiRewrites().Prepare(ProjectLoader.Load(path), step, "net10.0");

        Assert.Equal(2, changes.Count);
        Assert.EndsWith("ProductRepository.cs", changes[0].Path);
        var csproj = changes[1].NewContent!;
        Assert.Contains("<Reference Include=\"System.Runtime.Caching\" />", csproj);
        Assert.Contains("<ItemGroup Condition=\"'$(TargetFramework)' == 'net10.0'\">", csproj);
        Assert.Contains("<PackageReference Include=\"System.Runtime.Caching\" Version=\"10.0.0\" />", csproj);

        FileChange.ApplyAll(changes);
        Assert.Empty(new ApiRewrites().Prepare(ProjectLoader.Load(path), step, "net10.0")); // nothing left to do
    }

    [Fact]
    public void Planner_adds_the_rewrite_step_only_where_it_applies()
    {
        var plan = UpgradePlanner.Create(new DotnetLift.Core.SolutionAnalyzer().Analyze(Samples.LegacyShopSln));

        var step = Assert.Single(plan.Steps, s => s.Id == "api-rewrites");
        Assert.Equal("LegacyShop.Data", step.Project);
        var index = plan.Steps.ToList().IndexOf(step);
        Assert.Equal("retarget", plan.Steps[index - 1].Id);
        Assert.Equal("build", plan.Steps[index + 1].Id);
    }
}
