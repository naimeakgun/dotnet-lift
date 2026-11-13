# Roadmap

The goal for the first week: take a typical .NET Framework 4.8 solution (an ASP.NET MVC / Web API app plus the class libraries it uses) and move it to .NET 10 step by step, with the user approving each change or letting the tool run unattended.

| Version | What | Status |
|---|---|---|
| 0.1 | `analyze`: load .sln/.csproj without MSBuild, detect project kinds, scan packages, references, Web.config and source code, build a dependency-ordered plan, write a Markdown report, ask questions interactively | ✅ |
| 0.2 | `upgrade`: interactive step runner (approve / skip / approve all / quit), git checkpoint per step, first automatic steps: SDK-style conversion and packages.config → PackageReference | ⏳ |
| 0.3 | Retargeting (multi-target libraries while the web app is still on .NET Framework), `dotnet build` loop that reads compiler errors | ⏳ |
| 0.4 | Web project: Program.cs from Global.asax/App_Start, appsettings.json from Web.config, static files to wwwroot | ⏳ |
| 0.5 | LLM agent for agent-assisted steps: tool loop (read file, propose edit, build, read errors), diff preview and approval; works with Anthropic and OpenAI-compatible APIs | ⏳ |
| 0.6 | Controllers and views: System.Web rewrites, ApiController → ControllerBase, child actions → View Components | ⏳ |
| 1.0 | End-to-end: the LegacyShop sample upgraded to .NET 10 by the tool and running, docs and demo | ⏳ |

## Design principles

- **Deterministic first.** Anything that can be done with rules (project files, packages, config) is done without an LLM. The agent is only used for code that needs understanding.
- **Every step is reviewable.** Each step is its own git commit, so it can be inspected or reverted.
- **Never leave the solution unbuildable for long.** Libraries multi-target `net48;net10.0` until the web app has moved.
- **No MSBuild dependency for analysis.** Legacy project files are read as XML so the analysis runs on macOS and Linux too.
