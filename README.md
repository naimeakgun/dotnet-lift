# dotnet-lift

[![CI](https://github.com/naimeakgun/dotnet-lift/actions/workflows/ci.yml/badge.svg)](https://github.com/naimeakgun/dotnet-lift/actions/workflows/ci.yml)

**Upgrade .NET Framework 4.x solutions to modern .NET, one reviewable step at a time.**

`dotnet-lift` reads an ASP.NET MVC / Web API solution and the class libraries it uses, tells you exactly what stands between it and .NET 10, and builds an ordered upgrade plan. It asks questions as it goes, and the upgrade runs step by step: approve each change, or let it run unattended. Every applied step is its own git commit.

> Microsoft's .NET Upgrade Assistant is deprecated and its replacement needs a paid GitHub Copilot subscription. `dotnet-lift` is free, open source, runs from the command line on Windows, macOS and Linux, and does not depend on any one AI vendor.

## Status

Early, built in public. `analyze` works; `upgrade` automates the project-file steps (packages.config → PackageReference, legacy → SDK-style) and the build-and-fix loop is next. See the [roadmap](docs/ROADMAP.md).

## What it looks like

```console
$ dotnet-lift analyze samples/LegacyShop

Which .NET version do you want to move to?
  1) .NET 10 (LTS, supported until Nov 2028) - recommended
  2) .NET 11 (STS, supported until Nov 2028)
Choose [1-2] (default 1): 1

Projects (upgrade order):
  1. LegacyShop.Core              Class library            v4.8     2 blocker(s), 5 warning(s)
  2. LegacyShop.Data              Class library            v4.8     1 blocker(s), 9 warning(s)
  3. LegacyShop.Web               ASP.NET MVC + Web API    v4.8     3 blocker(s), 27 warning(s)

Plan:
   1. [auto ] [Low   ] LegacyShop.Core: Migrate packages.config to PackageReference
   2. [auto ] [Low   ] LegacyShop.Core: Convert to SDK-style project
   3. [agent] [High  ] LegacyShop.Core: Replace APIs removed from modern .NET
   4. [agent] [Medium] LegacyShop.Core: Decouple from ConfigurationManager
   5. [auto ] [Medium] LegacyShop.Core: Retarget to net48;net10.0
   6. [auto ] [Low   ] LegacyShop.Core: Build and run tests
   7. [auto ] [Low   ] LegacyShop.Data: Migrate packages.config to PackageReference
   8. [auto ] [Low   ] LegacyShop.Data: Convert to SDK-style project
   9. [agent] [High  ] LegacyShop.Data: Remove the System.Web dependency
  10. [agent] [Medium] LegacyShop.Data: Keep EF 6 running on modern .NET
  11. [auto ] [Medium] LegacyShop.Data: Retarget to net48;net10.0
  12. [auto ] [Low   ] LegacyShop.Data: Build and run tests
  13. [auto ] [High  ] LegacyShop.Web: Convert to an ASP.NET Core project (net10.0)
  14. [agent] [Medium] LegacyShop.Web: Move Global.asax and App_Start into Program.cs
  15. [auto ] [Low   ] LegacyShop.Web: Move Web.config settings to appsettings.json
  16. [agent] [High  ] LegacyShop.Web: Rebuild the request pipeline
  17. [agent] [High  ] LegacyShop.Web: Port controllers
  18. [agent] [High  ] LegacyShop.Web: Port Razor views
  19. [auto ] [Low   ] LegacyShop.Web: Move static files to wwwroot
  20. [human] [Low   ] LegacyShop.Web: Build, run and smoke test

Blockers:
  - LegacyShop.Core (Services/SnapshotSerializer.cs:13): BinaryFormatter is removed from modern .NET (it always throws since .NET 9).
  - LegacyShop.Core (Services/SnapshotSerializer.cs:22): BinaryFormatter is removed from modern .NET (it always throws since .NET 9).
  - LegacyShop.Data: Class library references System.Web. It cannot move to modern .NET until this coupling is removed.
  - LegacyShop.Web: Microsoft.AspNet.Web.Optimization 1.1.3: System.Web.Optimization bundling.
  - LegacyShop.Web (Controllers/HomeController.cs:18): [ChildActionOnly] child actions do not exist in ASP.NET Core.
  - LegacyShop.Web (Views/Shared/_Layout.cshtml:11): Html.Action / Html.RenderAction (child action call).

Write the full report to lift-report.md? [Y/n]:
```

The Markdown report lists every finding with file, line and a suggested fix, grouped under the step that resolves it.

### Upgrading

```console
$ dotnet-lift upgrade samples/LegacyShop --target net10.0
Git repository: /src/LegacyShop
20 step(s), 4 can be done automatically right now.

[1/20] LegacyShop.Core · Migrate packages.config to PackageReference  (automatic, low risk)
    ~ LegacyShop.Core/LegacyShop.Core.csproj (+1 −4)
    - LegacyShop.Core/packages.config (delete)
  Apply this step? [Y]es / [n]o, skip / [d]iff / [a]ll remaining / [q]uit: y
  ✓ Applied and committed.

[2/20] LegacyShop.Core · Convert to SDK-style project  (automatic, low risk)
    ~ LegacyShop.Core/LegacyShop.Core.csproj (+7 −30)
  Apply this step? [Y]es / [n]o, skip / [d]iff / [a]ll remaining / [q]uit: d
  ...
[3/20] LegacyShop.Core · Replace APIs removed from modern .NET  (agent-assisted, high risk)
  → Not automated yet; it stays in the report as a to-do.
```

*(Abridged output from CI.)*

- `--yes` applies every automatic step without asking, `--dry-run` only shows the diffs.
- Each applied step is one commit (`dotnet-lift: <step> (<project>)`), so `git revert` undoes exactly one step.
- The tool refuses to start on a working tree with uncommitted changes in unattended mode (`--allow-dirty` overrides).
- Running `upgrade` again is safe: steps that are already done disappear from the plan.

The SDK-style conversion keeps behaviour: the project still targets the same .NET Framework version, keeps its framework references, packages and project references, and files the old project did not compile get an explicit `<Compile Remove>`. CI converts the sample libraries with the tool and builds them on every push.

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
dotnet-lift upgrade [path] [options]

  path                 A .sln, a .csproj or a folder (default: current folder)
  -t, --target <tfm>   net10.0 (default) or net11.0. Asked interactively if omitted.
  -o, --output <file>  Where to write the Markdown report
      --no-report      Print the summary only
  -y, --yes            Do not ask questions; use defaults (upgrade: apply every automatic step)
      --dry-run        upgrade: show what would change, write nothing
      --allow-dirty    upgrade: run even with uncommitted changes
```

## Development

```bash
dotnet build DotnetLift.slnx
dotnet test DotnetLift.slnx
```

Analysis reads project files as XML instead of loading MSBuild, so it works on any OS even for projects that only build on Windows.

## License

MIT
