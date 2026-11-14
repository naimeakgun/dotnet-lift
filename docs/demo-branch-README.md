# LegacyShop, upgraded by dotnet-lift

This branch is **generated automatically** by the [`demo` workflow](https://github.com/naimeakgun/dotnet-lift/blob/main/.github/workflows/demo.yml)
on every push to `main`, and force-pushed. Do not commit here.

How it is made:

1. The first commit is the untouched .NET Framework 4.8 sample from [`samples/LegacyShop`](https://github.com/naimeakgun/dotnet-lift/tree/main/samples/LegacyShop):
   an ASP.NET MVC + Web API app (`LegacyShop.Web`) and two class libraries it uses (`LegacyShop.Core`, `LegacyShop.Data`).
2. `dotnet-lift upgrade . --yes` runs on it. Every step the tool applies is one commit, named `dotnet-lift: <step> (<project>)`.
3. `dotnet-lift analyze` writes [`lift-report.md`](lift-report.md): everything that is still left to do.
4. The converted libraries are built before the branch is pushed, so what you see here compiles.

**Browse the [commit list](https://github.com/naimeakgun/dotnet-lift/commits/demo/upgraded-sample)** and open each commit to see exactly what that step changed.

As the tool learns more steps (retargeting to .NET 10, fixing build errors, porting the web project), more commits appear here.
