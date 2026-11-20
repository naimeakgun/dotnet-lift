# dotnet-lift

[![CI](https://github.com/naimeakgun/dotnet-lift/actions/workflows/ci.yml/badge.svg)](https://github.com/naimeakgun/dotnet-lift/actions/workflows/ci.yml)

**Upgrade .NET Framework 4.x solutions to modern .NET, one reviewable step at a time.**

`dotnet-lift` reads an ASP.NET MVC / Web API solution and the class libraries it uses, tells you exactly what stands between it and .NET 10, and builds an ordered upgrade plan. It asks questions as it goes, and the upgrade runs step by step: approve each change, or let it run unattended. Every applied step is its own git commit.

> Microsoft's .NET Upgrade Assistant is deprecated and its replacement needs a paid GitHub Copilot subscription. `dotnet-lift` is free, open source and runs from the command line on Windows, macOS and Linux. It needs nothing but the .NET SDK: no API key, no account, no network. Where AI can help, you bring the assistant you already use.

## Status

Early, built in public. Working today:

- `analyze`: findings with file and line, an ordered plan, a Markdown report.
- `upgrade`: packages.config → PackageReference, legacy → SDK-style, retarget libraries to `net48;net10.0`, **Roslyn rewrites** for well-known APIs (`HttpRuntime.Cache` → `MemoryCache`, `HttpContext.Current.User` → a `CurrentUser` provider the web app sets at startup, and `BinaryFormatter` → `System.Text.Json` with explicit approval), then a **build → read errors → fix → rebuild** loop that fixes known errors (missing packages) and explains the ones that need a code change. For the web project: Web.config settings → **appsettings.json**.

On the sample, **both class libraries build for .NET 10** after `dotnet-lift upgrade` (and still for .NET Framework 4.8, so the old web app keeps working). The web project has started: its settings are in appsettings.json; static files, the project file and Program.cs are next. See the [roadmap](docs/ROADMAP.md).

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
22 step(s), 11 can be done automatically right now.

[1/22] LegacyShop.Core · Migrate packages.config to PackageReference  (automatic, low risk)
    ~ LegacyShop.Core/LegacyShop.Core.csproj (+1 −4)
    - LegacyShop.Core/packages.config (delete)
  Apply this step? [Y]es / [n]o, skip / [d]iff / [a]ll remaining / [q]uit: y
  ✓ Applied and committed.
[2/22] LegacyShop.Core · Convert to SDK-style project  (automatic, low risk)
    ~ LegacyShop.Core/LegacyShop.Core.csproj (+7 −30)
  ✓ Applied and committed.
[4/22] LegacyShop.Core · Retarget to net48;net10.0  (automatic, medium risk)
    ~ LegacyShop.Core/LegacyShop.Core.csproj (+4 −4)
  ✓ Applied and committed.
[5/22] LegacyShop.Core · Replace BinaryFormatter with System.Text.Json  (automatic, high risk)
    ~ LegacyShop.Core/Services/SnapshotSerializer.cs (+3 −3)
    ~ LegacyShop.Core/LegacyShop.Core.csproj (+1 −0)
  ⚠ This changes the serialized format. Anything the old code wrote with BinaryFormatter (cookies, files,
    cache entries, database columns) cannot be read by the new code. ...
  Apply this step? [y]es / [N]o, skip / [d]iff / [a]ll remaining / [q]uit: y
  ✓ Applied and committed.
[6/22] LegacyShop.Core · Build for net10.0 and fix errors  (automatic, low risk)
  Building for net10.0...
  Fix: Add package System.Configuration.ConfigurationManager 10.0.0 for net10.0  (resolves CS0103 ×2)
    ~ LegacyShop.Core/LegacyShop.Core.csproj (+4 −0)
  ✓ Applied and committed.
  Building again (attempt 2)...
  ✓ LegacyShop.Core builds for net10.0.
...
[12/22] LegacyShop.Data · Rewrite well-known APIs (Roslyn)  (automatic, low risk)
    ~ LegacyShop.Data/Repositories/ProductRepository.cs (+3 −4)
    ~ LegacyShop.Data/LegacyShop.Data.csproj (+5 −0)
  ✓ Applied and committed.
[13/22] LegacyShop.Data · Replace HttpContext.Current.User with a CurrentUser provider  (automatic, high risk)
    ~ LegacyShop.Data/Repositories/ProductRepository.cs (+3 −3)
    + LegacyShop.Data/Infrastructure/CurrentUser.cs (new)
    ~ LegacyShop.Data/LegacyShop.Data.csproj (+0 −1)
    ~ LegacyShop.Web/Global.asax.cs (+2 −0)
  ✓ Applied and committed.
[14/22] LegacyShop.Data · Build for net10.0 and fix errors  (automatic, low risk)
  Building for net10.0...
  ✓ LegacyShop.Data builds for net10.0.
...
[17/22] LegacyShop.Web · Move Web.config settings to appsettings.json  (automatic, low risk)
    + LegacyShop.Web/appsettings.json (new)
  ✓ Applied and committed.
...

[21/22] LegacyShop.Web · Move static files to wwwroot  (automatic, low risk)
  → Not yet: the project is still an ASP.NET (.NET Framework) project, which serves these files from their current folders. This step runs after the ASP.NET Core project step.
...

Applied 11, skipped 0, nothing to change 0, not automated yet 9, waiting for an earlier step 1.
Builds for net10.0: ok: LegacyShop.Core, LegacyShop.Data
```

*(Abridged output of a real CI run: the build-fix loop runs the real `dotnet build`.)*

- `--yes` applies every automatic step without asking, `--dry-run` only shows the diffs.
- Each applied step is one commit (`dotnet-lift: <step> (<project>)`), so `git revert` undoes exactly one step.
- Steps that change behaviour (today: the serialization format) are marked ⚠, default to **no** when asked, and are skipped by `--yes`. Add `--accept-risky` to apply them unattended.
- The tool refuses to start on a working tree with uncommitted changes in unattended mode (`--allow-dirty` overrides).
- Running `upgrade` again is safe: steps that are already done disappear from the plan.

**See it on the sample:** the [`demo/upgraded-sample`](https://github.com/naimeakgun/dotnet-lift/commits/demo/upgraded-sample) branch starts with the untouched .NET Framework 4.8 sample and has one commit per step the tool applied, plus the report of what is left. It is regenerated by CI on every push to `main`.

The `CurrentUser` step shows the approach for coupling that has no drop-in replacement: the library gets a tiny provider (`CurrentUser.Principal`), its code reads that instead of `HttpContext.Current.User`, and the web app sets the provider in `Application_Start` (`CurrentUser.Use(() => HttpContext.Current?.User)`), so behaviour does not change. Once nothing in the library needs `System.Web`, the reference is removed. A file that uses `HttpContext.Current` for anything else (`Items`, `Request`, `Session`) is left alone and reported.

Web.config settings move to `appsettings.json` under the keys `IConfiguration` reads, so `ConfigurationManager.AppSettings["VatRate"]` becomes `configuration["VatRate"]` with the same value:

```json
{
  "ConnectionStrings": {
    "ShopDb": "Data Source=.\\SQLEXPRESS;Initial Catalog=LegacyShop;Integrated Security=True",
    "ShopDb_ProviderName": "System.Data.SqlClient"
  },
  "ShopName": "Legacy Shop",
  "VatRate": "0.20",
  ...
}
```

The effective settings are read the way ASP.NET reads them (`add`, `remove` and `clear` in order, `configSource` and `file` followed); keys only the MVC 5 template needs (`webpages:*`, `ClientValidationEnabled`, ...) are dropped, and `Payment:Url` becomes a `Payment` section. A key that cannot be written as JSON (one that is both a value and a section) is listed in a comment at the top of the file instead of being lost. Web.config itself is not touched until the ASP.NET Core project replaces it, and running the step on an existing appsettings.json only adds what is missing. Config transforms (`Web.Release.config`) are not read yet.

Static files move into `wwwroot/` with their folders: `Content/site.css` becomes `wwwroot/Content/site.css`, which `UseStaticFiles` serves under the same URL, `/Content/site.css`, so views, stylesheets and bookmarks keep working without a change. `Content/`, `Scripts/`, `fonts/`, `Images/`, `img/`, `favicon.ico` and `robots.txt` are moved byte for byte (git records them as renames); code and configuration in those folders (`.cs`, `.config`, ...) stay, a file that already exists in `wwwroot/` is never overwritten, and explicit project items for a moved file are updated. The step waits until the web project is an ASP.NET Core project, because the old app serves these files from their current folders; until then `upgrade` shows it as `Not yet` with the reason.

How the build-fix loop works: it builds only the new target (`-f net10.0`), parses compiler, MSBuild and NuGet errors, and looks them up in a rule catalog. Known fixes (today: missing packages such as `System.Configuration.ConfigurationManager`, `System.Runtime.Caching`) are proposed like any other step, committed, and the project is rebuilt. Errors that need a code change are grouped and explained. Errors in a referenced project are reported as such instead of being "fixed" in the wrong place. The .NET Framework target is left alone, and CI checks that it still builds.

Code rewrites use Roslyn syntax trees, not text search: strings, comments and look-alike types are never touched, multi-line calls are handled, and only call shapes with an exact equivalent are rewritten (`HttpRuntime.Cache.Insert` with a cache dependency or a callback is left for the build step to report). The rewritten code has to build for both targets, so `System.Runtime.Caching` is added as a framework reference for net48 and as a package for net10.0.

The SDK-style conversion keeps behaviour: the project still targets the same .NET Framework version, keeps its framework references, packages and project references, and files the old project did not compile get an explicit `<Compile Remove>`. CI converts the sample libraries with the tool and builds them on every push, and a snapshot test compares the generated project files with [`samples/LegacyShop.Expected`](samples/LegacyShop.Expected).

## What it checks

| Area | Examples |
|---|---|
| Project files | Legacy vs SDK-style, target framework, `packages.config` |
| Packages | MVC 5, Web API 2, `System.Web.Optimization`, OWIN, SignalR 2, Unity/Autofac/Ninject integrations, EF 6, NuGet content packages (jQuery, bootstrap) |
| Framework references | `System.Web` in class libraries, `System.Configuration`, WCF, Remoting, COM+, `System.Drawing` |
| Web.config | appSettings, connection strings, Forms/Windows auth, session state, HTTP modules and handlers, custom errors, binding redirects, EF provider config |
| Data compatibility (C#, Roslyn) | Code that builds everywhere but produces different bytes on modern .NET: `Encoding.Default` / `GetEncoding(0)`, code pages without `CodePagesEncodingProvider`, `Encoding.UTF8.GetString` over hashes, culture-sensitive `ToLower()`/`ToUpper()`, `GetHashCode()` values that may be stored, compressed bytes compared exactly. Each finding says what to verify with golden vectors |
| Source code (C# and Razor) | `HttpContext.Current`, `HttpRuntime.Cache`, `Session[...]`, `Server.MapPath`, `FormsAuthentication`, `ConfigurationManager`, `BinaryFormatter`, child actions, bundling, `ApiController`, Global.asax registrations, `Thread.Abort`, `AppDomain.CreateDomain` |

## How the plan is built

1. Projects are sorted by their references, so a library is upgraded before anything that uses it.
2. While the web app is still on .NET Framework, libraries are multi-targeted (`net48;net10.0`) so the solution keeps building throughout.
3. Each step is marked **Automatic** (rule-based), **Agent-assisted** (a code rewrite you approve) or **Manual**, with a risk level raised to *High* whenever a blocker is involved.

### Central Package Management

```console
$ dotnet-lift cpm path/to/Solution.sln --dry-run   # show the diff
$ dotnet-lift cpm path/to/Solution.sln             # write Directory.Packages.props, one commit
```

Every `PackageReference` version moves into one `Directory.Packages.props` next to the solution; project files keep only `Include`. Nothing changes at build time: when projects used different versions of a package, the most common one becomes the central version and the others keep theirs with `VersionOverride` (listed, so aligning them is a conscious decision). Projects still on packages.config are not affected, and legacy project files that use `PackageReference` have to be converted to SDK-style first. Once CPM is on, every later `dotnet-lift upgrade` step that adds a package writes the version to `Directory.Packages.props` instead of the project file.

### DLL references → packages

```console
$ dotnet-lift refs --init-map    # writes dotnet-lift.packages.json: every DLL referenced by HintPath, with a guessed package
$ dotnet-lift refs --dry-run     # after checking the map against your feed
$ dotnet-lift refs               # one commit
```

DLLs referenced by file path outside `packages/` (a `SolutionItems` or `lib` folder) become `PackageReference`s. `dotnet-lift.packages.json` says which package each assembly comes from (`"Acme.Logging": "Acme.Logging@2.3.0"`, `"Acme.Core": "Acme.Core"` to take the version from the reference, or `"keep"` for a DLL that has no package); `--by-name` assumes package id = assembly name for the rest. A reference inside a conditional item group keeps its condition, versions go to `Directory.Packages.props` when CPM is on, and projects still on packages.config are left for later. The DLL files are never deleted; the tool lists the ones nothing references any more. `analyze` reports these references, and package versions fixed at `1.0.0` in a project file or `.nuspec`.

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
      --accept-risky   upgrade: also apply behaviour-changing steps without asking
```

## Development

```bash
dotnet build DotnetLift.slnx
dotnet test DotnetLift.slnx
```

Analysis reads project files as XML instead of loading MSBuild, so it works on any OS even for projects that only build on Windows.

## License

MIT
