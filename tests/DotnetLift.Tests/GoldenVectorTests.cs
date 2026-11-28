using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using DotnetLift.Core.Upgrade;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetLift.Tests;

/// <summary>Compiles the helper add-tests writes, as C# 7.3, and runs it the way a test project would.</summary>
public class GoldenVectorTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The helper compiled with the project folder in its metadata; NETFRAMEWORK defined to act as the .NET Framework build.</summary>
    private static Type Compile(string projectDir, bool netFramework)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3, preprocessorSymbols: netFramework ? ["NETFRAMEWORK"] : []);
        var metadata = $"[assembly: System.Reflection.AssemblyMetadata(\"{GoldenVectorSource.MetadataKey}\", @\"{projectDir}\")]";
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(GoldenVectorSource.Source("Acme.Core.Tests"), options),
            CSharpSyntaxTree.ParseText(metadata, options),
        };
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Golden" + Guid.NewGuid().ToString("N"), trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Location.IsInSource);

        var assembly = Assembly.Load(stream.ToArray());
        var type = assembly.GetTypes().SingleOrDefault(t => t.Name == "GoldenVector");
        Assert.True(type is not null, "GoldenVector not found; types: " + string.Join(", ", assembly.GetTypes().Select(t => t.FullName)));
        Assert.Equal("Acme.Core.Tests.GoldenVector", type!.FullName);
        return type;
    }

    private static void Check(Type helper, string name, object value)
    {
        var methods = helper.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Check").ToList();
        var method = methods.SingleOrDefault(m => m.GetParameters()[1].ParameterType.FullName == value.GetType().FullName);
        Assert.True(method is not null, "No Check overload for " + value.GetType() + "; found: " + string.Join(", ", methods));
        try
        {
            method!.Invoke(null, [name, value]);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
    }

    [Fact]
    public void Net_framework_records_and_modern_net_compares_byte_for_byte()
    {
        var dir = NewDir();
        var framework = Compile(dir, netFramework: true);
        var modern = Compile(dir, netFramework: false);

        Check(framework, "hash", "İsmail:şifre\r\n");
        Check(framework, "bytes", new byte[] { 0xDD, 0xFE, 0x00 });
        Assert.Equal(Encoding.UTF8.GetBytes("İsmail:şifre\r\n"), File.ReadAllBytes(Path.Combine(dir, "golden", "hash.txt")));
        Assert.Equal(new byte[] { 0xDD, 0xFE, 0x00 }, File.ReadAllBytes(Path.Combine(dir, "golden", "bytes.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "golden"), "*.tmp"));

        Check(modern, "hash", "İsmail:şifre\r\n");          // same output: passes
        Check(modern, "bytes", new byte[] { 0xDD, 0xFE, 0x00 });
        Check(framework, "hash", "İsmail:şifre\r\n");       // recorded vectors are compared on .NET Framework too

        var error = Assert.ThrowsAny<Exception>(() => Check(modern, "hash", "İsmail:sifre\r\n"));
        Assert.Equal("GoldenVectorException", error.GetType().Name);
        Assert.Contains("Golden vector hash.txt differs", error.Message);
        Assert.Contains("first difference at byte 8", error.Message); // "İsmail:" is 8 bytes in UTF-8, then ş (C5 9F) vs s
        Assert.Contains("expected text: \"\\u0130smail:\\u015fifre\\u000d\\u000a\"", error.Message); // İ, ş and CR LF escaped
        Assert.Contains("actual text:   \"\\u0130smail:sifre\\u000d\\u000a\"", error.Message);

        var changed = Assert.ThrowsAny<Exception>(() => Check(modern, "bytes", new byte[] { 0xDD, 0x3F, 0x00 }));
        Assert.Contains("expected: DD FE 00", changed.Message);
        Assert.Contains("actual:   DD 3F 00", changed.Message);
    }

    [Fact]
    public void Modern_net_does_not_record_a_missing_vector()
    {
        var dir = NewDir();
        var modern = Compile(dir, netFramework: false);

        var error = Assert.ThrowsAny<Exception>(() => Check(modern, "never-recorded", "x"));

        Assert.Contains("Vectors are recorded on .NET Framework", error.Message);
        Assert.False(Directory.Exists(Path.Combine(dir, "golden")));
    }

    [Fact]
    public void Names_must_be_file_names_and_text_must_round_trip()
    {
        var helper = Compile(NewDir(), netFramework: true);

        Assert.Throws<ArgumentException>(() => Check(helper, "../escape", "x"));
        var surrogate = Assert.Throws<ArgumentException>(() => Check(helper, "lone", "a\ud800b"));
        Assert.Contains("Encoding.Unicode.GetBytes", surrogate.Message);
    }
}
