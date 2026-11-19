using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetLift.Core;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Tests;

public class AppSettingsTests
{
    private static readonly JsonDocumentOptions AllowComments = new() { CommentHandling = JsonCommentHandling.Skip };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static WebConfigSettings Read(string webConfigXml, params (string Name, string Content)[] extraFiles)
    {
        var dir = TempDir();
        foreach (var (name, content) in extraFiles)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }
        var path = Path.Combine(dir, "Web.config");
        File.WriteAllText(path, webConfigXml);
        return AppSettingsJson.ReadWebConfig(path)!;
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json, documentOptions: AllowComments)!.AsObject();

    [Fact]
    public void Sample_settings_move_to_the_keys_IConfiguration_reads()
    {
        var settings = AppSettingsJson.ReadWebConfig(Path.Combine(Samples.LegacyShopDir, "LegacyShop.Web", "Web.config"))!;

        var json = Parse(AppSettingsJson.Render(null, settings)!);

        Assert.Equal(@"Data Source=.\SQLEXPRESS;Initial Catalog=LegacyShop;Integrated Security=True",
            (string?)json["ConnectionStrings"]!["ShopDb"]);
        Assert.Equal("System.Data.SqlClient", (string?)json["ConnectionStrings"]!["ShopDb_ProviderName"]);
        Assert.Equal("Legacy Shop", (string?)json["ShopName"]);
        Assert.Equal("0.20", (string?)json["VatRate"]); // configuration values are strings, as in Web.config
        Assert.Equal("https://sandbox.example-pay.com/api", (string?)json["PaymentGatewayUrl"]);
        Assert.False(json.ContainsKey("webpages:Version"));
        Assert.False(json.ContainsKey("webpages"));
        Assert.False(json.ContainsKey("ClientValidationEnabled"));
        Assert.Equal("*", (string?)json["AllowedHosts"]);
        Assert.Equal("Information", (string?)json["Logging"]!["LogLevel"]!["Default"]);
    }

    [Fact]
    public void Remove_and_clear_are_applied_in_order_and_the_last_add_wins()
    {
        var settings = Read("""
            <configuration>
              <connectionStrings>
                <clear />
                <add name="Main" connectionString="old" />
                <add name="Main" connectionString="new" />
                <add name="Gone" connectionString="x" />
                <remove name="Gone" />
              </connectionStrings>
              <appSettings>
                <add key="Dropped" value="1" />
                <clear />
                <add key="A" value="1" />
                <add key="b" value="2" />
                <add key="B" value="3" />
                <remove key="A" />
              </appSettings>
            </configuration>
            """);

        Assert.Equal(new KeyValuePair<string, string>[] { new("b", "3") }, settings.AppSettings);
        Assert.Equal(new[] { new ConnectionStringSetting("Main", "new", null) }, settings.ConnectionStrings);
    }

    [Fact]
    public void A_colon_in_a_key_becomes_a_section_and_collisions_are_noted_not_lost()
    {
        var settings = Read("""
            <configuration>
              <appSettings>
                <add key="Payment:Url" value="https://pay" />
                <add key="Payment:Timeout" value="30" />
                <add key="Payment" value="on" />
              </appSettings>
            </configuration>
            """);

        var text = AppSettingsJson.Render(null, settings)!;
        var json = Parse(text);

        Assert.Equal("https://pay", (string?)json["Payment"]!["Url"]);
        Assert.Equal("30", (string?)json["Payment"]!["Timeout"]);
        Assert.StartsWith("// dotnet-lift: not moved, because a key cannot be both a value and a section in JSON: Payment.", text);
    }

    [Fact]
    public void ConfigSource_and_file_are_followed()
    {
        var settings = Read("""
            <configuration>
              <connectionStrings configSource="connections.config" />
              <appSettings file="secrets.config">
                <add key="Key" value="from web.config" />
                <add key="Other" value="kept" />
              </appSettings>
            </configuration>
            """,
            ("connections.config", """<connectionStrings><add name="Db" connectionString="Server=db" providerName="System.Data.SqlClient" /></connectionStrings>"""),
            ("secrets.config", """<appSettings><add key="Key" value="from file" /></appSettings>"""));

        Assert.Equal(new KeyValuePair<string, string>[] { new("Key", "from file"), new("Other", "kept") }, settings.AppSettings);
        Assert.Equal(new[] { new ConnectionStringSetting("Db", "Server=db", "System.Data.SqlClient") }, settings.ConnectionStrings);
    }

    [Fact]
    public void A_missing_external_file_contributes_nothing()
    {
        var settings = Read("""
            <configuration>
              <appSettings file="missing.config"><add key="A" value="1" /></appSettings>
              <connectionStrings configSource="missing.config" />
            </configuration>
            """);

        Assert.Equal(new KeyValuePair<string, string>[] { new("A", "1") }, settings.AppSettings);
        Assert.Empty(settings.ConnectionStrings);
    }

    [Fact]
    public void An_existing_appsettings_json_keeps_its_values_and_only_gains_missing_keys()
    {
        var settings = new WebConfigSettings(
            [new("ShopName", "From Web.config"), new("VatRate", "0.20")],
            [new ConnectionStringSetting("ShopDb", "Server=old", null)]);
        const string existing = """
            {
              // edited by hand
              "ShopName": "Edited",
              "ConnectionStrings": { "Other": "Server=other" },
            }
            """;

        var json = Parse(AppSettingsJson.Render(existing, settings)!);

        Assert.Equal("Edited", (string?)json["ShopName"]);
        Assert.Equal("0.20", (string?)json["VatRate"]);
        Assert.Equal("Server=other", (string?)json["ConnectionStrings"]!["Other"]);
        Assert.Equal("Server=old", (string?)json["ConnectionStrings"]!["ShopDb"]);
        Assert.False(json.ContainsKey("Logging")); // template defaults are only for a new file
    }

    [Fact]
    public void Nothing_to_add_or_an_unreadable_file_means_no_change()
    {
        var settings = new WebConfigSettings([new("ShopName", "x")], []);

        Assert.Null(AppSettingsJson.Render("""{ "shopname": "already there" }""", settings)); // keys are case-insensitive
        Assert.Null(AppSettingsJson.Render("{ not json", settings));
        Assert.Null(AppSettingsJson.Render("[]", settings));
    }

    [Fact]
    public void A_Web_config_that_is_not_xml_is_not_read()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Web.config");
        File.WriteAllText(path, "<configuration><appSettings>");

        Assert.Null(AppSettingsJson.ReadWebConfig(path));
    }

    [Fact]
    public void Step_writes_appsettings_json_once_and_leaves_Web_config_alone()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        var analysis = new SolutionAnalyzer().Analyze(Path.Combine(dir, "LegacyShop.sln"));
        var plan = UpgradePlanner.Create(analysis);
        var step = plan.Steps.Single(s => s.Id == "appsettings");
        var web = analysis.Solution.Projects.Single(p => p.Name == step.Project);
        var executor = Assert.IsType<WebConfigToAppSettings>(StepExecutors.For(step, web));
        var webConfigBefore = File.ReadAllText(web.WebConfigPath!);

        var changes = executor.Prepare(web, step, "net10.0");

        var change = Assert.Single(changes);
        Assert.Equal(Path.Combine(web.Directory, "appsettings.json"), change.Path);
        Assert.True(change.IsCreate);
        FileChange.ApplyAll(changes);
        Assert.Empty(executor.Prepare(web, step, "net10.0"));
        Assert.Equal(webConfigBefore, File.ReadAllText(web.WebConfigPath!));
    }
}
