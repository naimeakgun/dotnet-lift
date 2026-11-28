using DotnetLift.Core;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Tests;

public class MoreRulesTests
{
    /// <summary>A net48 SDK-style library with one source file and optional extra project-file content.</summary>
    private static string Library(string source, string projectItems = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Acme.Lib"));
        File.WriteAllText(Path.Combine(dir, "Acme.Lib", "Acme.Lib.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net48</TargetFramework>
              </PropertyGroup>
              <ItemGroup>{projectItems}
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Acme.Lib", "Code.cs"), source);
        return dir;
    }

    private static List<Finding> Analyze(string dir) => new SolutionAnalyzer().Analyze(dir).Findings.ToList();

    private static int LineOf(string source, string text) =>
        source.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;

    private const string Legacy = """
        using System;
        using System.Configuration;
        using System.Net;
        using System.Runtime.Remoting.Messaging;
        using System.Runtime.Remoting.Proxies;
        using System.Web.Script.Serialization;
        using Microsoft.Practices.EnterpriseLibrary.Data;
        using Microsoft.Win32;

        namespace Acme.Lib
        {
            // RealProxy and CallContext.LogicalGetData( in a comment do not count
            public class TracingProxy : RealProxy { }

            public class Remote : MarshalByRefObject { }

            public class ShopSection : ConfigurationSection { }

            public static class Legacy
            {
                public static object Correlation() => CallContext.LogicalGetData("correlation");

                public static void Tls() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }

                public static string Json(object value) => new JavaScriptSerializer().Serialize(value);

                public static object Excel() => System.Runtime.InteropServices.Marshal.GetActiveObject("Excel.Application");

                public static object Setting() => Registry.LocalMachine.OpenSubKey("Software\\Acme");

        #if NET6_0
                public static string Runtime => "net6";
        #elif NET6_0_OR_GREATER
                public static string Runtime => "modern";
        #endif

                [System.Runtime.InteropServices.DllImport("user32.dll")]
                private static extern int MessageBeep(uint type);

                public static string Mail(object model) => RazorEngine.Engine.Razor.RunCompile("template", "key", null, model);
            }
        }
        """;

    [Theory]
    [InlineData("Removed API", "RealProxy (remoting proxies)", "public class TracingProxy : RealProxy")]
    [InlineData("Removed API", "CallContext (System.Runtime.Remoting.Messaging)", "CallContext.LogicalGetData(\"correlation\")")]
    [InlineData("Removed API", "MarshalByRefObject", "public class Remote : MarshalByRefObject")]
    [InlineData("Removed API", "Marshal.GetActiveObject", "GetActiveObject(\"Excel.Application\")")]
    [InlineData("Removed API", "JavaScriptSerializer", "new JavaScriptSerializer()")]
    [InlineData("Obsolete API", "ServicePointManager", "ServicePointManager.SecurityProtocol")]
    [InlineData("Configuration", "Custom configuration section", "ShopSection : ConfigurationSection")]
    [InlineData("Dependency", "Enterprise Library (Microsoft.Practices.EnterpriseLibrary)", "using Microsoft.Practices.EnterpriseLibrary.Data;")]
    [InlineData("Conditional compilation", "Version-exact preprocessor symbol", "#if NET6_0")]
    [InlineData("Windows-only", "Windows-only API", "Registry.LocalMachine")]
    [InlineData("Windows-only", "P/Invoke", "DllImport(\"user32.dll\")")]
    [InlineData("Templating", "Razor template engine", "Engine.Razor.RunCompile")]
    public void Each_rule_points_at_its_line(string category, string message, string lineText)
    {
        var findings = Analyze(Library(Legacy));

        var finding = Assert.Single(findings, f => f.Category == category && f.Message.StartsWith(message, StringComparison.Ordinal) && f.File == "Code.cs");
        Assert.Equal(LineOf(Legacy, lineText), finding.Line);
        Assert.False(string.IsNullOrWhiteSpace(finding.Suggestion));
    }

    [Fact]
    public void Or_greater_symbols_and_comments_are_not_findings()
    {
        var findings = Analyze(Library(Legacy));

        Assert.Single(findings, f => f.Category == "Conditional compilation"); // #elif NET6_0_OR_GREATER is fine
        Assert.DoesNotContain(findings, f => f.Line == LineOf(Legacy, "in a comment do not count"));
    }

    [Fact]
    public void Removed_apis_become_a_planned_task()
    {
        var dir = Library(Legacy);

        var step = Assert.Single(UpgradePlanner.Create(new SolutionAnalyzer().Analyze(dir)).Steps, s => s.Id == "removed-apis");

        Assert.Contains(step.Findings, f => f.Message.StartsWith("RealProxy", StringComparison.Ordinal));
        Assert.Contains(step.Findings, f => f.Message.StartsWith("CallContext", StringComparison.Ordinal));
    }

    [Fact]
    public void Inventory_counts_com_references_enterprise_library_and_system_drawing()
    {
        const string source = """
            using System.Drawing;
            using System.Drawing.Imaging;

            namespace Acme.Lib
            {
                public static class Thumbnails
                {
                    public static void Make(string input, string output)
                    {
                        using (var image = Image.FromFile(input))
                        using (var bitmap = new Bitmap(image, new Size(120, 80)))
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.DrawString("Acme", new Font("Arial", 8), Brushes.White, new PointF(2, 2));
                            bitmap.Save(output, ImageFormat.Png);
                        }
                    }
                }
            }
            """;
        var items = """

                <COMReference Include="Excel">
                  <Guid>{00020813-0000-0000-C000-000000000046}</Guid>
                </COMReference>
                <PackageReference Include="EnterpriseLibrary.Data" Version="6.0.1304" />
            """;

        var findings = Analyze(Library(source, items));

        var com = Assert.Single(findings, f => f.Message.StartsWith("1 COM reference(s)", StringComparison.Ordinal));
        Assert.Contains("Excel", com.Message);
        Assert.Single(findings, f => f.Message == "Enterprise Library: EnterpriseLibrary.Data.");
        var drawing = Assert.Single(findings, f => f.Message.StartsWith("System.Drawing is used", StringComparison.Ordinal));
        Assert.Equal("System.Drawing is used in 1 file(s): Bitmap ×1, Brushes ×1, Font ×1, Graphics ×1, Image ×1, ImageFormat ×1.", drawing.Message);
        Assert.Equal(Severity.Warning, drawing.Severity);
    }

    [Fact]
    public void The_sample_gets_none_of_the_new_findings()
    {
        var findings = new SolutionAnalyzer().Analyze(Samples.LegacyShopSln).Findings;

        Assert.DoesNotContain(findings, f => f.Category is "Windows-only" or "Conditional compilation" or "Templating");
        Assert.DoesNotContain(findings, f => f.Message.StartsWith("Enterprise Library", StringComparison.Ordinal));
    }
}
