using DotnetLift.Cli;
using DotnetLift.Core;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;

namespace DotnetLift.Tests;

public class RequestContextTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MigrationProfile Profile(string json)
    {
        var path = Path.Combine(TempDir(), MigrationProfile.FileName);
        File.WriteAllText(path, json);
        return MigrationProfile.Load(path);
    }

    private const string Full = """
        {
          "requestContext": {
            "user": "UserContext.Current.Principal",
            "items": "RequestContext.Current.Items",
            "session": "SessionAccessor.Current",
            "context": "RequestContext.Current",
            "usings": ["Acme.Web.Abstractions"]
          }
        }
        """;

    [Fact]
    public void Mapped_members_and_null_checks_move_to_the_team_abstractions()
    {
        const string source = """
            using System;
            using System.Web;

            namespace Acme.Lib
            {
                public class Audit
                {
                    public string Who() =>
                        HttpContext.Current != null ? HttpContext.Current.User.Identity.Name : "system";

                    public object Tenant() => HttpContext.Current.Items["tenant"];

                    public object Cart() => System.Web.HttpContext.Current.Session["cart"];
                }
            }
            """;

        var result = RequestContextRewrite.Rewrite(source, Profile(Full).RequestContext!)!;
        var text = result.NewText.Replace("\r\n", "\n");

        Assert.Equal(4, result.Replaced);
        Assert.Contains("RequestContext.Current != null ? UserContext.Current.Principal.Identity.Name : \"system\";", text);
        Assert.Contains("public object Tenant() => RequestContext.Current.Items[\"tenant\"];", text);
        Assert.Contains("public object Cart() => SessionAccessor.Current[\"cart\"];", text);
        Assert.Contains("using System;\nusing Acme.Web.Abstractions;\n", text); // replaces `using System.Web;`
        Assert.DoesNotContain("System.Web", text);
        Assert.DoesNotContain("HttpContext", text);
    }

    [Fact]
    public void A_member_without_a_mapping_leaves_the_whole_file_alone()
    {
        const string source = """
            using System.Web;
            class C
            {
                object U() => HttpContext.Current.User;
                void R() { HttpContext.Current.Response.Redirect("/"); }
            }
            """;

        var result = RequestContextRewrite.Rewrite(source, Profile(Full).RequestContext!)!;

        Assert.Equal(0, result.Replaced);
        Assert.Equal(2, result.LeftAlone);
        Assert.Equal(source, result.NewText);
    }

    [Fact]
    public void Null_checks_need_the_context_expression()
    {
        const string source = "using System.Web; class C { bool A() => HttpContext.Current != null && HttpContext.Current.User != null; }";
        var map = Profile("""{ "requestContext": { "user": "UserContext.Current.Principal" } }""").RequestContext!;

        Assert.Equal(0, RequestContextRewrite.Rewrite(source, map)!.Replaced);
    }

    [Fact]
    public void Complex_expressions_are_parenthesized()
    {
        var map = Profile("""{ "requestContext": { "user": "accessor?.User" } }""").RequestContext!;

        var text = RequestContextRewrite.Rewrite("using System.Web; class C { string N() => HttpContext.Current.User.Identity.Name; }", map)!.NewText;

        Assert.Contains("=> (accessor?.User).Identity.Name;", text);
    }

    [Theory]
    [InlineData("""{ "requestContext": { "cookies": "X.Y" } }""", "unknown setting requestContext.cookies")]
    [InlineData("""{ "requestContext": { "user": "not an expression;" } }""", "requestContext.user must be a C# expression")]
    [InlineData("""{ "requestContext": { "usings": ["A"] } }""", "needs at least one member")]
    [InlineData("""{ "requestContext": "UserContext" }""", "\"requestContext\" must be an object")]
    public void Broken_settings_stop_with_a_clear_error(string json, string message)
    {
        var path = Path.Combine(TempDir(), MigrationProfile.FileName);
        File.WriteAllText(path, json);

        var error = Assert.Throws<InvalidDataException>(() => MigrationProfile.Load(path));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task Upgrade_with_the_profile_uses_the_team_abstractions_instead_of_a_generated_provider()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        File.WriteAllText(Path.Combine(dir, MigrationProfile.FileName), """
            { "target": "net10.0",
              "requestContext": { "user": "UserContext.Current.Principal", "context": "UserContext.Current", "usings": ["Acme.Web.Abstractions"] } }
            """);
        Git.InitWithCommit(dir);

        var plan = UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir), MigrationProfile.Find(dir));
        Assert.Single(plan.Steps, s => s.Id == "request-context" && s.Project == "LegacyShop.Data");
        Assert.DoesNotContain(plan.Steps, s => s.Id == "current-user");

        await new App(new StringReader(""), new StringWriter(), interactive: false, new FakeBuilder()).RunAsync(["upgrade", dir, "--yes"]);

        var repository = File.ReadAllText(Path.Combine(dir, "LegacyShop.Data", "Repositories", "ProductRepository.cs"));
        Assert.Contains("UserContext.Current != null && UserContext.Current.Principal != null", repository);
        Assert.Contains("? UserContext.Current.Principal.Identity.Name", repository);
        Assert.Contains("using Acme.Web.Abstractions;", repository);
        Assert.DoesNotContain("HttpContext", repository);
        Assert.False(File.Exists(Path.Combine(dir, "LegacyShop.Data", "Infrastructure", "CurrentUser.cs")));
        Assert.Contains("dotnet-lift: Replace HttpContext.Current with the team's request abstractions (LegacyShop.Data)", Git.LogSubjects(dir));
    }
}
