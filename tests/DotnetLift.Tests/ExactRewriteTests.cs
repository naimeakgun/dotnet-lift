using System.Reflection;
using DotnetLift.Core;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;
using DotnetLift.Core.Upgrade;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetLift.Tests;

public class ExactRewriteTests
{
    private const string Ns = "Acme.Lib.Infrastructure";

    private const string Source = """
        using System.Runtime.Remoting.Messaging;
        using System.Web.Security;

        namespace Acme.Lib
        {
            public static class Session
            {
                public static void Begin(string id) { CallContext.LogicalSetData("correlation", id); }
                public static string Id => (string)CallContext.LogicalGetData("correlation");
                public static void End() { System.Runtime.Remoting.Messaging.CallContext.FreeNamedDataSlot("correlation"); }
                public static string Hash(string password) => FormsAuthentication.HashPasswordForStoringInConfigFile(password, "SHA1");
            }
        }
        """;

    [Fact]
    public void Calls_move_to_the_helpers_and_the_old_usings_go()
    {
        var result = LegacyApiRewrite.Rewrite(Source, Ns)!;
        var text = result.NewText.Replace("\r\n", "\n");

        Assert.Equal(4, result.Replaced);
        Assert.Equal(LegacyHelpers.LogicalCallContext | LegacyHelpers.PasswordHash, result.Helpers);
        Assert.Contains("LogicalCallContext.SetData(\"correlation\", id);", text);
        Assert.Contains("(string)LogicalCallContext.GetData(\"correlation\");", text);
        Assert.Contains("LogicalCallContext.FreeNamedDataSlot(\"correlation\");", text);
        Assert.Contains("LegacyPasswordHash.HashPasswordForStoringInConfigFile(password, \"SHA1\");", text);
        Assert.StartsWith($"using {Ns};\n\nnamespace Acme.Lib", text);
    }

    [Fact]
    public void Calls_without_an_exact_equivalent_stay()
    {
        const string source = """
            using System.Runtime.Remoting.Messaging;
            using System.Web.Security;
            class C
            {
                object A() => CallContext.GetData("x");                                        // does not flow: not the same as AsyncLocal
                string B(string p) => FormsAuthentication.HashPasswordForStoringInConfigFile(p, format);
                void C1() { FormsAuthentication.SignOut(); }
                static string format = "SHA1";
            }
            """;

        Assert.Null(LegacyApiRewrite.Rewrite(source, Ns));
    }

    [Fact]
    public void Helpers_are_CSharp_7_3_and_behave_like_the_framework()
    {
        var assembly = Compile(LegacyApiRewrite.CallContextSource(Ns), LegacyApiRewrite.PasswordHashSource(Ns));
        var hash = assembly.GetType($"{Ns}.LegacyPasswordHash")!.GetMethod("HashPasswordForStoringInConfigFile")!;
        string Hash(string p, string f) => (string)hash.Invoke(null, [p, f])!;

        Assert.Equal("E5E9FA1BA31ECD1AE84F75CAAA474F3A663F05F4", Hash("secret", "SHA1"));
        Assert.Equal("5EBE2294ECD0E0F08EAB7690D2A6EE69", Hash("secret", "md5"));

        var context = assembly.GetType($"{Ns}.LogicalCallContext")!;
        void Set(string n, object? v) => context.GetMethod("SetData")!.Invoke(null, [n, v]);
        object? Get(string n) => context.GetMethod("GetData")!.Invoke(null, [n]);

        Set("correlation", "abc");
        Assert.Equal("abc", Task.Run(() => Get("correlation")).Result);      // flows into tasks
        Task.Run(() => Set("correlation", "changed")).Wait();
        Assert.Equal("abc", Get("correlation"));                              // changes there do not flow back
        context.GetMethod("FreeNamedDataSlot")!.Invoke(null, ["correlation"]);
        Assert.Null(Get("correlation"));
        Assert.Null(Get("never-set"));
    }

    [Fact]
    public void The_step_rewrites_the_library_and_adds_the_helpers_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Acme.Lib"));
        File.WriteAllText(Path.Combine(dir, "Acme.Lib", "Acme.Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net48</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(dir, "Acme.Lib", "Session.cs"), Source);

        var step = Assert.Single(UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir)).Steps, s => s.Id == "exact-rewrites");
        var changes = new ExactRewrites().Prepare(SolutionLoader.Load(dir).Projects.Single(), step, "net10.0");

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.Path.EndsWith(Path.Combine("Infrastructure", "LogicalCallContext.cs"), StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Path.EndsWith(Path.Combine("Infrastructure", "LegacyPasswordHash.cs"), StringComparison.Ordinal));
        FileChange.ApplyAll(changes);
        Assert.Empty(new ExactRewrites().Prepare(SolutionLoader.Load(dir).Projects.Single(), step, "net10.0"));
    }

    private static Assembly Compile(params string[] sources)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Exact" + Guid.NewGuid().ToString("N"),
            sources.Select(s => CSharpSyntaxTree.ParseText(s, options)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(stream.ToArray());
    }
}
