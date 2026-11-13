# dotnet-lift

**Upgrade .NET Framework 4.x solutions to modern .NET, one reviewable step at a time.**

`dotnet-lift` reads an ASP.NET MVC / Web API solution and the class libraries it uses, tells you exactly what stands between it and .NET 10, and builds an ordered upgrade plan. It asks questions as it goes, and the upgrade (coming next) runs step by step: approve each change, or let it run unattended.

> Microsoft's .NET Upgrade Assistant is deprecated and its replacement needs a paid GitHub Copilot subscription. `dotnet-lift` is free, open source, runs from the command line on Windows, macOS and Linux, and does not depend on any one AI vendor.

## Status

Early, built in public. `analyze` works today; `upgrade` is being built this week. See the [roadmap](docs/ROADMAP.md).

## What it looks like

```console
$ dotnet-lift analyze samples/LegacyShop

Which .NET version do you want to move to?
  1) .NET 10 (LTS, supported until Nov 2028) - recommended
  2) .NET 11 (STS, supported until Nov 2028)
Choose [1-2] (default 1):

Projects (upgrade order):
  1. LegacyShop.Core     Class library           v4.8   1 blocker(s), 3 warning(s)
  2. LegacyShop.Data     Class library           v4.8   1 blocker(s), 6 warning(s)
  3. LegacyShop.Web      ASP.NET MVC + Web API   v4.8   4 blocker(s), 25 warning(s)

Plan:
   1. [auto ] [Low   ] LegacyShop.Core: Convert to SDK-style project
   2. [auto ] [Low   ] LegacyShop.Core: Migrate packages.config to PackageReference
   3. [agent] [High  ] LegacyShop.Core: Replace APIs removed from modern .NET
   ...
   9. [agent] [High  ] LegacyShop.Data: Remove the System.Web dependency
   ...

Blockers:
  - LegacyShop.Core (Services/SnapshotSerializer.cs:13): BinaryFormatter is removed from modern .NET ...
  - LegacyShop.Data: Class library references System.Web ...
  - LegacyShop.Web (Views/Shared/_Layout.cshtml:11): Html.Action / Html.RenderAction (child action call).

Write the full report to lift-report.md? [Y/n]:
```

*(Counts above are illustrative; run it on the sample to see the real output.)*

The Markdown report lists every finding with file, line and a suggested fix, grouped under the step that resolves it.

## What it checks

| Area | Examples |
|---|---|
| Project files | Legacy vs SDK-style, target framework, `packages.config` |
| Packages | MVC 5, Web API 2, `System.Web.Optimization`, OWIN, SignalR 2, Unity/Autofac/Ninject integrations, EF 6, NuGet content packages (jQuery, bootstrap) |
| Framework references | `System.Web` in class libraries, `System.Configuration`, WCF, Remoting, COM+, `System.Drawing` |
| Web.config | appSettings, connection strings, Forms/Windows auth, session state, HTTP modules and handlers, custom errors, binding redirects, EF provider config |
| Source code (C# and Razor) | `HttpContext.Current`, `HttpRuntime.Cache`, `Session[...]`, `Server.MapPath`, `FormsAuthentication`, `ConfigurationManager`, `BinaryFormatter`, child actions, bundling, `ApiController`, Global.asax registrations, `Thread.Abort`, `AppDomain.CreateDomain` |

## How the plan is built

1. Projects are sorted by their references, so a library is upgraded before anything that uses it.
2. While the web app is still on .NET Framework, libraries are multi-targeted (`net48;net10.0`) so the solution keeps building throughout.
3. Each step is marked **Automatic** (rule-based), **Agent-assisted** (a code rewrite you approve) or **Manual**, with a risk level raised to *High* whenever a blocker is involved.

## Try it

Requires the .NET 10 SDK.

```bash
git clone https://github.com/naimeakgun/dotnet-lift
cd dotnet-lift
dotnet run --project src/DotnetLift.Cli -- analyze samples/LegacyShop
```

`samples/LegacyShop` is a small but realistic .NET Framework 4.8 solution (MVC + Web API web app, a core library and an EF 6 data library) full of the patterns real legacy apps have.

### Options

```
dotnet-lift analyze [path] [options]

  path                 A .sln, a .csproj or a folder (default: current folder)
  -t, --target <tfm>   net10.0 (default) or net11.0. Asked interactively if omitted.
  -o, --output <file>  Where to write the Markdown report
      --no-report      Print the summary only
  -y, --yes            Do not ask questions; use defaults
```

## Development

```bash
dotnet build DotnetLift.slnx
dotnet test DotnetLift.slnx
```

Analysis reads project files as XML instead of loading MSBuild, so it works on any OS even for projects that only build on Windows.

## License

MIT
