using DotnetLift.Core.Model;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Core.Analyzers;

/// <summary>
/// Third-party packages that need a major upgrade (or a replacement) before they run on modern .NET, with the breaking
/// changes the upgrade brings. Package upgrades are a large share of a migration and easy to underestimate: each finding
/// names the version to move to and what changes in the code, so it can be estimated and planned as its own task.
/// </summary>
public sealed class PackageUpgradeAdvisor : IAnalyzer
{
    public const string Category = "Package upgrade";

    /// <param name="Id">Package id (exact, case-insensitive).</param>
    /// <param name="Below">Versions below this need the upgrade; null = every version (the package is replaced).</param>
    /// <param name="Target">What to move to: a version line of the same package or another package.</param>
    /// <param name="Changes">The breaking changes to expect.</param>
    private sealed record Rule(string Id, string? Below, Severity Severity, string Target, string Changes);

    private static readonly Rule[] Rules =
    [
        new("NEST", null, Severity.Warning, "Elastic.Clients.Elasticsearch (the 8.x+ client; NEST is deprecated)",
            "New client with a different API: requests are built with descriptors from generated types, aggregations and queries change shape, and NEST's high-level mapping attributes are gone. Plan it as a rewrite of the search layer, behind an interface."),
        new("Elasticsearch.Net", null, Severity.Warning, "Elastic.Clients.Elasticsearch (or Elastic.Transport for raw requests)",
            "The low-level client is deprecated with NEST; raw JSON requests move to the new client's transport."),
        new("Quartz", "3.0.0", Severity.Warning, "Quartz 3.x",
            "Quartz 2.x is .NET Framework only. In 3.x the API is async: IJob.Execute returns Task, scheduler calls are awaited, Common.Logging is replaced by Microsoft.Extensions.Logging, and the ADO.NET job store tables need the 3.x schema changes. Hosting moves to Quartz.Extensions.Hosting."),
        new("StackExchange.Redis", "2.0.0", Severity.Warning, "StackExchange.Redis 2.x",
            "1.x works but is unmaintained; 2.x uses System.IO.Pipelines and changes connection timeouts/backlog behaviour. Check ConnectionMultiplexer settings (syncTimeout, asyncTimeout) and test reconnect behaviour."),
        new("StackExchange.Redis.StrongName", null, Severity.Warning, "StackExchange.Redis 2.x (it is strong-named itself)",
            "The StrongName package is deprecated: replace the id, the namespaces stay the same."),
        new("Npgsql", "6.0.0", Severity.Warning, "Npgsql 8.x or later",
            "Npgsql 6.0 changed date/time mapping: 'timestamp with time zone' requires DateTime with Kind=Utc and returns UTC, 'timestamp' maps to Kind=Unspecified. Stored values and comparisons can shift by the UTC offset; Npgsql.EnableLegacyTimestampBehavior restores the old mapping while code is fixed. Verify with golden vectors over real rows."),
        new("Hangfire.Core", "1.7.0", Severity.Warning, "Hangfire 1.8.x with Hangfire.AspNetCore",
            "Configuration moves from OWIN (app.UseHangfireServer / UseHangfireDashboard in Startup) to services.AddHangfire(...) + AddHangfireServer() and app.UseHangfireDashboard() in Program.cs. Check the dashboard authorization filter and job serializer settings (TypeNameHandling)."),
        new("Hangfire", "1.7.0", Severity.Warning, "Hangfire 1.8.x with Hangfire.AspNetCore",
            "Same as Hangfire.Core: configuration moves from OWIN to AddHangfire/AddHangfireServer in Program.cs."),
        new("MassTransit", "8.0.0", Severity.Warning, "MassTransit 8.x (v9 and later need a commercial licence)",
            "v8 consolidates configuration into AddMassTransit(x => ...) with the hosted service built in; consumer, saga and transport configuration APIs changed, and several packages (MassTransit.AspNetCore, MassTransit.Extensions.DependencyInjection) were merged into MassTransit. Message contracts stay compatible on the wire."),
        new("mongocsharpdriver", null, Severity.Warning, "MongoDB.Driver (the legacy driver package is not supported)",
            "The legacy API (MongoServer, MongoDatabase, Query builders) is gone: code moves to IMongoClient / IMongoCollection<T> and the Builders<T> API."),
        new("MongoDB.Driver", "3.0.0", Severity.Warning, "MongoDB.Driver 3.x",
            "3.0 removes the legacy API and the GuidRepresentationMode setting: GUIDs default to the standard (subtype 4) representation. Existing documents written with C# legacy GUIDs (subtype 3) read differently unless the serializer is configured with GuidRepresentation.CSharpLegacy; check this before deploying."),
        new("SSH.NET", "2020.0.0", Severity.Warning, "SSH.NET 2024.x or later",
            "Old versions miss modern key exchange and host key algorithms that current servers require. Newer versions changed some APIs (ConnectionInfo, PrivateKeyFile constructors, async methods); check the connection setup and key loading."),
        new("EntityFramework", "6.4.0", Severity.Warning, "EntityFramework 6.5.x",
            "EF 6 runs on modern .NET from 6.3; 6.4/6.5 fix most issues. Provider registration and connection strings move from App.config to a DbConfiguration class, EDMX designer files need the EF 6 tooling to regenerate."),
        new("Microsoft.Owin.Security.Jwt", null, Severity.Warning, "Microsoft.AspNetCore.Authentication.JwtBearer",
            "OWIN JWT middleware becomes AddAuthentication().AddJwtBearer(...). Since .NET 8 it validates with JsonWebTokenHandler: claim type mapping and the token type passed to events differ (JsonWebToken instead of JwtSecurityToken); compare the claims your code reads."),
        new("Microsoft.Owin.Security.OAuth", null, Severity.Warning, "an OAuth server library (OpenIddict, Duende IdentityServer) plus JwtBearer",
            "The OWIN OAuth authorization server has no ASP.NET Core equivalent: token issuing moves to a dedicated library, token validation to AddJwtBearer. Tokens issued by the old server cannot be validated by the new one."),
        new("System.IdentityModel.Tokens.Jwt", "6.0.0", Severity.Warning, "System.IdentityModel.Tokens.Jwt 8.x, or Microsoft.IdentityModel.JsonWebTokens",
            "5.x is old and unmaintained. 7.x+ changed claim mapping defaults and moved to JsonWebTokenHandler; versions of the Microsoft.IdentityModel.* packages must match each other."),
        new("SerilogWeb.Classic", null, Severity.Warning, "Serilog.AspNetCore",
            "SerilogWeb.Classic hooks into System.Web. Use builder.Host.UseSerilog() / AddSerilog() and app.UseSerilogRequestLogging(); enrichers for HTTP properties come from Serilog.AspNetCore."),
        new("Serilog", "2.10.0", Severity.Info, "Serilog 4.x with matching sink versions",
            "Old Serilog runs, but current sinks need Serilog 3/4. Upgrade Serilog and every Serilog.Sinks.* / Serilog.Enrichers.* package together; Serilog 4 removes some obsolete overloads."),
        new("log4net", "2.0.10", Severity.Warning, "log4net 2.0.15 or later (or 3.x)",
            "Versions before 2.0.8 have no .NET Standard build. Configuration from App.config needs XmlConfigurator with a log4net.config file; to use ILogger, add Microsoft.Extensions.Logging.Log4Net.AspNetCore."),
        new("NLog", "4.5.0", Severity.Info, "NLog 5.x with NLog.Web.AspNetCore",
            "NLog 5 changed some defaults (no automatic config file in the app directory search for all hosts, KeepFileOpen, layout renderers). NLog.Web (System.Web) becomes NLog.Web.AspNetCore."),
        new("RestSharp", "107.0.0", Severity.Warning, "RestSharp 112.x or later",
            "107 rewrote RestSharp on HttpClient: IRestClient/IRestRequest interfaces are gone, Execute* methods are async (ExecuteAsync / GetAsync), authenticators and serializers are configured on RestClientOptions. Every call site changes."),
        new("RabbitMQ.Client", "7.0.0", Severity.Info, "RabbitMQ.Client 7.x",
            "6.x works on modern .NET. 7.0 is async-first: IModel became IChannel, CreateConnection/CreateModel became CreateConnectionAsync/CreateChannelAsync, consumers are AsyncEventingBasicConsumer. Plan it separately from the framework move."),
        new("Swashbuckle", null, Severity.Warning, "Swashbuckle.AspNetCore (or Microsoft.AspNetCore.OpenApi)",
            "Swashbuckle for Web API 2 does not run on ASP.NET Core. Configuration moves to AddSwaggerGen / UseSwagger in Program.cs; XML comment and operation filters need porting."),
        new("WindowsAzure.Storage", null, Severity.Warning, "Azure.Storage.Blobs / Azure.Storage.Queues / Azure.Data.Tables",
            "The legacy storage SDK is retired. The new SDKs use BlobServiceClient / BlobContainerClient and Azure.Identity credentials; paging, metadata and exception types (RequestFailedException) change."),
        new("WindowsAzure.ServiceBus", null, Severity.Warning, "Azure.Messaging.ServiceBus",
            "The .NET Framework Service Bus SDK is retired. ServiceBusClient / ServiceBusSender / ServiceBusProcessor replace QueueClient and MessagingFactory; message properties and settlement APIs differ."),
        new("Microsoft.Azure.ServiceBus", null, Severity.Warning, "Azure.Messaging.ServiceBus",
            "Microsoft.Azure.ServiceBus is deprecated. ServiceBusClient / ServiceBusSender / ServiceBusProcessor replace QueueClient / MessageReceiver."),
        new("Oracle.ManagedDataAccess", null, Severity.Warning, "Oracle.ManagedDataAccess.Core",
            "The .NET Framework package does not run on modern .NET. The Core package has the same namespaces; tnsnames/sqlnet configuration moves from App.config to OracleConfiguration or TNS_ADMIN."),
        new("Common.Logging", null, Severity.Info, "Microsoft.Extensions.Logging",
            "Common.Logging is unmaintained; libraries that need it (old Quartz, Spring.NET) move off it in their modern versions."),
    ];

    public string Name => "package-upgrades";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        foreach (var package in project.Packages)
        {
            var rule = Rules.FirstOrDefault(r => r.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                continue;
            }

            var trimmed = (package.Version ?? "").Trim().TrimStart('[', '(');
            var known = trimmed.Length > 0 && char.IsDigit(trimmed[0]);
            if (rule.Below is not null && known && VersionComparer.Instance.Compare(trimmed.Split(',')[0], rule.Below) >= 0)
            {
                continue; // already on a version that runs on modern .NET
            }

            var version = known ? package.Version : "(version not in the project file)";
            yield return new Finding(project.Name, rule.Severity, Category,
                $"{package.Id} {version} → {rule.Target}.",
                rule.Changes);
        }
    }
}
