# Roadmap

The goal for the first week: take a typical .NET Framework 4.8 solution (an ASP.NET MVC / Web API app plus the class libraries it uses) and move it to .NET 10 step by step, with the user approving each change or letting the tool run unattended.

## Next up, in priority order

Driven by a real migration: several .NET Framework 4.8 repositories, shared libraries published to a private NuGet feed, apps that consume them, and data (password hashes, serialized objects) that must stay byte-for-byte compatible. Work goes top to bottom; the web project items (0.5.3 onwards) wait behind P1–P7.

| # | Item | Why it comes here |
|---|---|---|
| P1 ✅ | **Data-compatibility analysis.** Findings, with what to verify, for code whose output silently changes between .NET Framework and modern .NET: `Encoding.Default` / `GetEncoding(0)` (the ANSI code page on .NET Framework, always UTF-8 on modern .NET), code-page encodings used without registering `CodePagesEncodingProvider`, `Encoding.UTF8.GetString` over binary data such as hashes (ill-formed UTF-8 is replaced differently since .NET Core 3.0), culture-sensitive `ToLower()`/`ToUpper()` (Turkish İ/ı), `GetHashCode()` results that are stored or sent, and compressed output compared byte for byte | Hashes and stored data that change without an error are the most expensive migration bug; this has to be known before any code moves |
| P2 ✅ | **Central Package Management.** Generate `Directory.Packages.props` from every `PackageReference`, report version conflicts, remove the `Version` attributes, set `ManagePackageVersionsCentrally` | Wanted in every repository before the move, and it makes P3 easier |
| P3 ✅ | **File references → packages.** `HintPath` DLLs outside `packages/` (for example a `SolutionItems` folder) become `PackageReference`s, mapped by a small mapping file or by assembly name; hard-coded package versions (such as a fixed `1.0.0`) are reported | Shared libraries must come from the feed, not from copied DLLs |
| P4 🚧 | **Cleanup steps.** ✅ `dotnet-lift cleanup`: remove references nothing uses (checked with Roslyn: leftover System.Web.Mvc, System.Web.Optimization, Owin, System.EnterpriseServices, Unity) and the `MigrationBackup` / `_UpgradeReport_Files` folders left by Visual Studio. ✅ `analyze` reports the same unused references as info findings (same check, nothing changed). Next: remove a project from the solution after listing what depends on it | Everything removed is something that does not have to be migrated |
| P5 | **Test projects.** Recognise MSTest / xUnit / NUnit projects, convert them to SDK-style with the right test packages, and run `dotnet test` after the build loop | Tests have to survive the upgrade and prove it |
| P6 | **Security findings.** Exception messages returned to clients (`ex.Message` in a response), secrets in config files | Cheap to find, and migration reviews look for them anyway |
| P7 | **Binary dependency inventory.** Referenced DLLs that target only .NET Framework (read from the assembly itself), as a list of vendor SDKs to request | Vendors take a long time to answer |
| P8 | **Web project, keeping JSON identical.** When the web project moves, keep Newtonsoft.Json with Web API 2 behaviour (PascalCase names, same date handling) so JavaScript clients keep working; then 0.5.3 → 0.6 below | JavaScript clients break on camelCase property names |
| later | 0.4 (prompt export for any AI assistant, MCP server mode) | Useful once the rule-based steps above are in place |

## Versions

| Version | What | Status |
|---|---|---|
| 0.1 | `analyze`: load .sln/.csproj without MSBuild, detect project kinds, scan packages, references, Web.config and source code, build a dependency-ordered plan, write a Markdown report, ask questions interactively | ✅ |
| 0.2 | `upgrade`: interactive step runner (yes / skip / diff / all / quit, `--yes`, `--dry-run`), one git commit per step, automatic packages.config → PackageReference and legacy → SDK-style conversion for class libraries | ✅ |
| 0.3 | Retarget libraries (`net48;net10.0`, framework references kept for net48 only) and the **build → read errors → fix → rebuild** loop: errors parsed from `dotnet build`, fixes from a rule catalog (missing packages), guidance for the rest, failing dependencies detected | ✅ |
| 0.3.1 | Roslyn rewrite step: `HttpRuntime.Cache` / `HttpContext.Current.Cache` → `MemoryCache.Default`, builds for net48 and net10.0 | ✅ |
| 0.3.2 | Explicit-approval steps (⚠, default "no", skipped by `--yes`, `--accept-risky` to opt in) and the `BinaryFormatter` → `System.Text.Json` rewrite. The sample's core library builds for net10.0 | ✅ |
| 0.3.3 | `HttpContext.Current.User` → a generated `CurrentUser` provider, wired into `Application_Start`; `System.Web` removed from libraries. **Milestone: both sample libraries build for net10.0** | ✅ |
| 0.4 | For errors the catalog cannot fix, two ways that need no API key: (1) `dotnet-lift fix --prompt` writes a ready-to-paste prompt per remaining error (error, code, suggested approach) for whatever AI assistant you already use; (2) an MCP server mode so Claude Code, Cursor or VS Code Copilot can drive analyze → upgrade → build with the user's own assistant doing the fixes | ⏳ |
| 0.5 | Web project: ASP.NET Core project file, Program.cs from Global.asax/App_Start, appsettings.json from Web.config, static files to wwwroot | 🚧 |
| 0.5.1 | `appsettings.json` from Web.config: appSettings (framework-only keys dropped, `a:b` keys become sections) and connection strings (provider name kept as `<name>_ProviderName`), `add`/`remove`/`clear`, `configSource` and `file` followed. Web.config is not changed; an existing appsettings.json keeps its values | ✅ |
| 0.5.2 | Static files: `Content/`, `Scripts/`, `fonts/`, `Images/`, `img/`, `favicon.ico`, `robots.txt` → `wwwroot/` with their folders, so URLs (and the references in views) stay the same. Byte-for-byte moves that git records as renames, nothing in `wwwroot/` overwritten, explicit project items updated. Waits for 0.5.3: the old app still serves the files from their current folders | ✅ |
| 0.5.3 | ASP.NET Core project file (`Microsoft.NET.Sdk.Web`, net10.0, project references kept, MVC 5 / Web API 2 / bundling packages dropped) | ⏳ |
| 0.5.4 | `Program.cs` from Global.asax and App_Start: routes (`MapRoute` → `MapControllerRoute`), Web API routes, global filters, the `CurrentUser` wiring | ⏳ |
| 0.6 | Controllers and views: System.Web rewrites, ApiController → ControllerBase, child actions → View Components; tests and smoke tests after each step | ⏳ |
| later | Optional built-in LLM agent (Anthropic / OpenAI-compatible / local Ollama) for fully unattended fixing. Strictly opt-in: without a key nothing is lost | 💭 |
| 1.0 | End-to-end: the LegacyShop sample upgraded to .NET 10 by the tool and running, docs and demo | ⏳ |

## Design principles

- **Works for everyone, with nothing but the .NET SDK.** No API key, account, paid service or network access is needed for any feature. AI help is always optional and always brought by the user.
- **Deterministic first.** Anything that can be done with rules (project files, packages, config) is done without an LLM. The agent is only used for code that needs understanding.
- **Every step is reviewable.** Each step is its own git commit, so it can be inspected or reverted.
- **Never leave the solution unbuildable for long.** Libraries multi-target `net48;net10.0` until the web app has moved.
- **No MSBuild dependency for analysis.** Legacy project files are read as XML so the analysis runs on macOS and Linux too.
