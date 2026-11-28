# dotnet-lift upgrade report

- **Input:** `LegacyShop.sln`
- **Target:** `net10.0`
- **Generated:** 2026-11-28 13:33

**3 project(s), 14 step(s).** Findings: 3 blocker, 35 warning, 24 info.

## Projects

| Order | Project | Type | Current | Format | Blockers | Warnings |
|---:|---|---|---|---|---:|---:|
| 1 | LegacyShop.Core | Class library | net48;net10.0 | SDK-style | 0 | 5 |
| 2 | LegacyShop.Data | Class library | net48;net10.0 | SDK-style | 0 | 2 |
| 3 | LegacyShop.Web | ASP.NET MVC + Web API | v4.8 | Legacy | 3 | 28 |

Projects are upgraded in dependency order: a project is only touched after everything it references.

```
LegacyShop.Core
LegacyShop.Data  ->  LegacyShop.Core
LegacyShop.Web  ->  LegacyShop.Core, LegacyShop.Data
```

## Upgrade plan

| # | Project | Step | Mode | Risk | Findings |
|---:|---|---|---|---|---:|
| 1 | LegacyShop.Core | Decouple from ConfigurationManager | Agent-assisted | Medium | 3 |
| 2 | LegacyShop.Core | Retarget to net48;net10.0 | Automatic | Medium | 0 |
| 3 | LegacyShop.Core | Build for net10.0 and fix errors | Automatic | Low | 0 |
| 4 | LegacyShop.Data | Keep EF 6 running on modern .NET | Agent-assisted | Medium | 1 |
| 5 | LegacyShop.Data | Retarget to net48;net10.0 | Automatic | Medium | 0 |
| 6 | LegacyShop.Data | Build for net10.0 and fix errors | Automatic | Low | 0 |
| 7 | LegacyShop.Web | Convert to an ASP.NET Core project (net10.0) | Automatic | High | 20 |
| 8 | LegacyShop.Web | Move Global.asax and App_Start into Program.cs | Agent-assisted | Medium | 5 |
| 9 | LegacyShop.Web | Move Web.config settings to appsettings.json | Automatic | Low | 4 |
| 10 | LegacyShop.Web | Rebuild the request pipeline | Agent-assisted | High | 5 |
| 11 | LegacyShop.Web | Port controllers | Agent-assisted | High | 13 |
| 12 | LegacyShop.Web | Port Razor views | Agent-assisted | High | 3 |
| 13 | LegacyShop.Web | Move static files to wwwroot | Automatic | Low | 3 |
| 14 | LegacyShop.Web | Build, run and smoke test | Manual | Low | 0 |

*Automatic*: deterministic change. *Agent-assisted*: code rewrite proposed for your approval. *Manual*: needs a human decision.

## LegacyShop.Core

### 1. Decouple from ConfigurationManager

_Agent-assisted · risk medium_

Short term: reference System.Configuration.ConfigurationManager. Better: receive settings through constructor parameters or IOptions<T>.

### 2. Retarget to net48;net10.0

_Automatic · risk medium_

Multi-target net48 and net10.0: code that still runs on .NET Framework keeps working while it is being migrated, and the library is ready for the new one. Framework-only references are limited to net48.

### 3. Build for net10.0 and fix errors

_Automatic · risk low_

Build the net10.0 target, fix known errors automatically (for example a missing package) and rebuild, then list the errors that need a code change with what to do about them.

#### Findings

| Severity | Where | Issue | Suggested fix |
|---|---|---|---|
| ⚠️ Warning |  | References System.Configuration (ConfigurationManager). | Short term: add the System.Configuration.ConfigurationManager package. Long term: bind settings with IConfiguration / IOptions<T>. |
| ⚠️ Warning | `Services/AppSettings.cs:12` | ConfigurationManager.AppSettings / ConnectionStrings. | Bind to IConfiguration / IOptions<T>; values move to appsettings.json. |
| ⚠️ Warning | `Services/AppSettings.cs:19` | ConfigurationManager.AppSettings / ConnectionStrings. | Bind to IConfiguration / IOptions<T>; values move to appsettings.json. |
| ⚠️ Warning | `Services/PasswordHasher.cs:14` | Encoding.Default is the machine's ANSI code page on .NET Framework (e.g. windows-1254 on a Turkish server) but always UTF-8 on modern .NET: bytes differ for every non-ASCII character. | Name the encoding the stored data was written with, e.g. Encoding.GetEncoding(1254) (register CodePagesEncodingProvider on modern .NET). Verify with golden vectors that contain non-ASCII characters (ç ğ ı İ ö ş ü). |
| ⚠️ Warning | `Services/PasswordHasher.cs:16` | UTF-8 decoding of binary data (hash): bytes that are not valid UTF-8 become U+FFFD, and since .NET Core 3.0 an invalid sequence can turn into a different number of U+FFFD characters than on .NET Framework, so the resulting string (and any hash or comparison built on it) can differ. | Keep stored values readable: compare golden vectors from the .NET Framework build. For new code, encode binary data as hex or Base64 (Convert.ToHexString / Convert.ToBase64String) instead of decoding it as text. |
| ℹ️ Info | `Services/PasswordHasher.cs:14` | 1 culture-sensitive ToLower()/ToUpper() call(s) in this file. Results depend on the current culture (Turkish İ/ı), and modern .NET uses ICU where .NET Framework used Windows NLS, so some results can change. | Use ToLowerInvariant()/ToUpperInvariant() for identifiers and keys, or pass an explicit CultureInfo. Where the old behaviour must be kept exactly (e.g. text that is hashed), compare golden vectors; the System.Globalization.UseNls runtime option restores NLS on Windows. |
| ℹ️ Info | `Services/PasswordHasher.cs:22` | userName.GetHashCode(): for strings this is randomized per process on modern .NET (and may differ between frameworks for other types). | Fine for dictionaries; never store, send or compare its value across processes. Use a stable hash (e.g. SHA-256, or XxHash from System.IO.Hashing) for anything persisted. |

## LegacyShop.Data

### 4. Keep EF 6 running on modern .NET

_Agent-assisted · risk medium_

Update to the latest EF 6.x and move provider registration from App.config into a DbConfiguration class. EF Core is a separate project.

### 5. Retarget to net48;net10.0

_Automatic · risk medium_

Multi-target net48 and net10.0: code that still runs on .NET Framework keeps working while it is being migrated, and the library is ready for the new one. Framework-only references are limited to net48.

### 6. Build for net10.0 and fix errors

_Automatic · risk low_

Build the net10.0 target, fix known errors automatically (for example a missing package) and rebuild, then list the errors that need a code change with what to do about them.

#### Findings

| Severity | Where | Issue | Suggested fix |
|---|---|---|---|
| ⚠️ Warning |  | EntityFramework 6.4.4: Entity Framework 6. | EF 6.4+ runs on modern .NET (config moves to code: DbConfiguration). Migrating to EF Core is a separate, larger decision. |
| ⚠️ Warning |  | References System.Configuration (ConfigurationManager). | Short term: add the System.Configuration.ConfigurationManager package. Long term: bind settings with IConfiguration / IOptions<T>. |
| ℹ️ Info | `App.config` | Has App.config. Class libraries do not read their own App.config at runtime. | Move any settings to the host application's configuration and delete the file if it only holds binding redirects or EF providers. |

## LegacyShop.Web

### 7. Convert to an ASP.NET Core project (net10.0)

_Automatic · risk high_

Switch the project to Microsoft.NET.Sdk.Web, remove MVC 5 / Web API 2 / bundling packages, keep the project references.

### 8. Move Global.asax and App_Start into Program.cs

_Agent-assisted · risk medium_

Translate route, filter, Web API and DI registrations into the minimal hosting model (WebApplication.CreateBuilder).

### 9. Move Web.config settings to appsettings.json

_Automatic · risk low_

Copy appSettings and connection strings into appsettings.json, where IConfiguration reads them under the same keys (a ':' in a key becomes a section, provider names are kept as <name>_ProviderName). Framework-only keys and binding redirects are dropped. Web.config stays as it is, so the old app keeps working; an existing appsettings.json keeps its values.

### 10. Rebuild the request pipeline

_Agent-assisted · risk high_

Authentication, session, HTTP modules/handlers and error pages become middleware registered in Program.cs.

### 11. Port controllers

_Agent-assisted · risk high_

MVC and Web API controllers move to Microsoft.AspNetCore.Mvc. ApiController becomes ControllerBase + [ApiController]; System.Web calls are rewritten.

### 12. Port Razor views

_Agent-assisted · risk high_

Views/Web.config becomes _ViewImports.cshtml, child actions become View Components, bundling calls become tags.

### 13. Move static files to wwwroot

_Automatic · risk low_

Content/, Scripts/, fonts/, Images/, img/, favicon.ico and robots.txt move into wwwroot/ with their folders, so every URL stays the same (UseStaticFiles serves wwwroot/Content/site.css as /Content/site.css) and views need no change. Files are moved, not rewritten; files that already exist in wwwroot stay where they are. Runs once the project is an ASP.NET Core project, because the old app serves them from their current folders. Client libraries from NuGet content packages (jQuery, Bootstrap) can later come from LibMan or npm.

### 14. Build, run and smoke test

_Manual · risk low_

dotnet build, start the app and request the main routes. Compare responses with the old application.

#### Findings

| Severity | Where | Issue | Suggested fix |
|---|---|---|---|
| 🛑 Blocker |  | Microsoft.AspNet.Web.Optimization 1.1.3: System.Web.Optimization bundling. | No direct equivalent. Use LigerShark.WebOptimizer, a frontend build step (npm/Vite) or plain <link>/<script> tags. |
| 🛑 Blocker | `Controllers/HomeController.cs:18` | [ChildActionOnly] child actions do not exist in ASP.NET Core. | Convert the action into a View Component. |
| 🛑 Blocker | `Views/Shared/_Layout.cshtml:11` | Html.Action / Html.RenderAction (child action call). | Replace with a View Component: @await Component.InvokeAsync("Name"). |
| ⚠️ Warning |  | EntityFramework 6.4.4: Entity Framework 6. | EF 6.4+ runs on modern .NET (config moves to code: DbConfiguration). Migrating to EF Core is a separate, larger decision. |
| ⚠️ Warning |  | Microsoft.AspNet.Mvc 5.2.9: ASP.NET MVC 5 package. | Remove. MVC is part of ASP.NET Core (Microsoft.NET.Sdk.Web). |
| ⚠️ Warning |  | Microsoft.AspNet.WebApi 5.2.9: ASP.NET Web API 2 package. | Remove. Web API controllers become ASP.NET Core controllers ([ApiController]). |
| ⚠️ Warning |  | Microsoft.AspNet.WebApi.Core 5.2.9: ASP.NET Web API 2 package. | Remove. Web API controllers become ASP.NET Core controllers ([ApiController]). |
| ⚠️ Warning |  | Microsoft.AspNet.WebApi.WebHost 5.2.9: ASP.NET Web API 2 package. | Remove. Web API controllers become ASP.NET Core controllers ([ApiController]). |
| ⚠️ Warning |  | Unity.Mvc5 1.2.3: Unity MVC integration. | Use the built-in Microsoft.Extensions.DependencyInjection container, or Unity.Microsoft.DependencyInjection. |
| ⚠️ Warning |  | References System.Configuration (ConfigurationManager). | Short term: add the System.Configuration.ConfigurationManager package. Long term: bind settings with IConfiguration / IOptions<T>. |
| ⚠️ Warning | `Controllers/CartController.cs:17` | Session[...] indexer. | ASP.NET Core session stores byte[]/string only: add AddSession()/UseSession() and serialize objects (e.g. to JSON). |
| ⚠️ Warning | `Controllers/CartController.cs:19` | Session[...] indexer. | ASP.NET Core session stores byte[]/string only: add AddSession()/UseSession() and serialize objects (e.g. to JSON). |
| ⚠️ Warning | `Controllers/CartController.cs:20` | Session[...] indexer. | ASP.NET Core session stores byte[]/string only: add AddSession()/UseSession() and serialize objects (e.g. to JSON). |
| ⚠️ Warning | `Controllers/CartController.cs:39` | FormsAuthentication. | Use cookie authentication: AddAuthentication().AddCookie() and HttpContext.SignInAsync/SignOutAsync. |
| ⚠️ Warning | `Controllers/HomeController.cs:13` | ConfigurationManager.AppSettings / ConnectionStrings. | Bind to IConfiguration / IOptions<T>; values move to appsettings.json. |
| ⚠️ Warning | `Controllers/HomeController.cs:21` | Session[...] indexer. | ASP.NET Core session stores byte[]/string only: add AddSession()/UseSession() and serialize objects (e.g. to JSON). |
| ⚠️ Warning | `Controllers/HomeController.cs:27` | Server.MapPath. | Use IWebHostEnvironment.WebRootPath / ContentRootPath. |
| ⚠️ Warning | `Controllers/ProductsApiController.cs:9` | Web API 2 controller (ApiController). | Derive from ControllerBase, add [ApiController]. IHttpActionResult becomes IActionResult. |
| ⚠️ Warning | `Global.asax.cs:15` | Global.asax / App_Start registration. | Moves to Program.cs: builder.Services.AddControllersWithViews(), app.MapControllerRoute(...), builder.Services.AddControllers(o => o.Filters.Add(...)). |
| ⚠️ Warning | `Global.asax.cs:16` | Global.asax / App_Start registration. | Moves to Program.cs: builder.Services.AddControllersWithViews(), app.MapControllerRoute(...), builder.Services.AddControllers(o => o.Filters.Add(...)). |
| ⚠️ Warning | `Global.asax.cs:17` | Global.asax / App_Start registration. | Moves to Program.cs: builder.Services.AddControllersWithViews(), app.MapControllerRoute(...), builder.Services.AddControllers(o => o.Filters.Add(...)). |
| ⚠️ Warning | `Global.asax.cs:18` | Global.asax / App_Start registration. | Moves to Program.cs: builder.Services.AddControllersWithViews(), app.MapControllerRoute(...), builder.Services.AddControllers(o => o.Filters.Add(...)). |
| ⚠️ Warning | `Global.asax.cs:20` | HttpContext.Current (static request access). | Inject IHttpContextAccessor, or better, pass the data you need into the method. |
| ⚠️ Warning | `LegacyShop.Web.csproj` | Legacy (non SDK-style) project file. | Convert to an SDK-style project. Explicit <Compile Include> lists, HintPaths and MSBuild imports go away. |
| ⚠️ Warning | `Views/Shared/_Layout.cshtml:6` | Scripts.Render / Styles.Render (System.Web.Optimization). | Use <script>/<link> tags with asp-append-version, or a bundler such as WebOptimizer. |
| ⚠️ Warning | `Views/Shared/_Layout.cshtml:16` | Scripts.Render / Styles.Render (System.Web.Optimization). | Use <script>/<link> tags with asp-append-version, or a bundler such as WebOptimizer. |
| ⚠️ Warning | `Web.config` | 3 application setting(s) in <appSettings>: ShopName, VatRate, PaymentGatewayUrl. | Move to appsettings.json and read them through IConfiguration / IOptions<T>. |
| ⚠️ Warning | `Web.config` | 1 connection string(s): ShopDb. | Move to the ConnectionStrings section of appsettings.json (and secrets out of source control). |
| ⚠️ Warning | `Web.config` | Forms authentication configured in Web.config. | Configure cookie authentication in Program.cs (AddAuthentication().AddCookie(o => o.LoginPath = ...)). |
| ⚠️ Warning | `Web.config` | HTTP module(s) registered: RequestTimingModule. | Rewrite each module as ASP.NET Core middleware. |
| ⚠️ Warning | `packages.config` | Uses packages.config. | Migrate to <PackageReference> items; transitive dependencies no longer need to be listed. |
| ℹ️ Info |  | Targets .NET Framework (v4.8). | Retarget to net10.0. |
| ℹ️ Info |  | Antlr 3.5.0.2: Bundling dependency (via WebGrease). | Remove. |
| ℹ️ Info |  | bootstrap 3.4.1: Client-side library delivered as a NuGet content package. | Content packages do not work with PackageReference. Use LibMan or npm. |
| ℹ️ Info |  | jQuery 3.4.1: Client-side library delivered as a NuGet content package. | Content packages do not work with PackageReference. Use LibMan or npm. |
| ℹ️ Info |  | Microsoft.AspNet.Razor 3.2.9: Razor for MVC 5. | Remove. Razor is built into ASP.NET Core. |
| ℹ️ Info |  | Microsoft.AspNet.WebApi.Client 5.2.9: Web API client (HttpClient formatting helpers). | Replace with System.Net.Http.Json, or keep Microsoft.AspNet.WebApi.Client which supports .NET. |
| ℹ️ Info |  | Microsoft.AspNet.WebPages 3.2.9: ASP.NET Web Pages. | Remove. Not needed in ASP.NET Core. |
| ℹ️ Info |  | Microsoft.CodeDom.Providers.DotNetCompilerPlatform 2.0.1: Roslyn CodeDOM provider for runtime view compilation. | Remove. |
| ℹ️ Info |  | Microsoft.Web.Infrastructure 2.0.0: System.Web infrastructure helper. | Remove. |
| ℹ️ Info |  | Modernizr 2.8.3: Client-side library delivered as a NuGet content package. | Content packages do not work with PackageReference. Use LibMan or npm. |
| ℹ️ Info |  | WebGrease 1.6.0: Bundling dependency. | Remove. |
| ℹ️ Info | `Controllers/CartController.cs:22` | System.Web HttpCookie. | Use Response.Cookies.Append(name, value, new CookieOptions { ... }). |
| ℹ️ Info | `Controllers/CartController.cs:24` | System.Web HttpCookie. | Use Response.Cookies.Append(name, value, new CookieOptions { ... }). |
| ℹ️ Info | `Controllers/CartController.cs:32` | Request.QueryString / Request.Form indexer. | Prefer model binding; otherwise Request.Query[...] / Request.Form[...] (StringValues). |
| ℹ️ Info | `Controllers/ProductsApiController.cs:20` | IHttpActionResult. | Use IActionResult or ActionResult<T>. |
| ℹ️ Info | `Global.asax.cs:14` | AreaRegistration. | Use [Area] attributes and an area route in Program.cs. |
| ℹ️ Info | `LegacyShop.Web.csproj` | Unity (dependency injection) is referenced but not used (reference Unity.Mvc5, package Unity, package Unity.Mvc5). | Run `dotnet-lift cleanup` to remove it before the upgrade; findings about these packages and assemblies then go away too. |
| ℹ️ Info | `Web.config` | Session state mode 'InProc'. | Add AddDistributedMemoryCache()/AddSession() (or a Redis/SQL distributed cache) and UseSession(). |
| ℹ️ Info | `Web.config` | <customErrors> configured. | Use app.UseExceptionHandler("/Error") and app.UseStatusCodePagesWithReExecute(...). |
| ℹ️ Info | `Web.config` | 2 assembly binding redirect(s). | Not needed on modern .NET; drop them. |
| ℹ️ Info | `Web.config` | <entityFramework> provider configuration in config file. | EF 6 on modern .NET does not read config files: register providers in a DbConfiguration class. |

