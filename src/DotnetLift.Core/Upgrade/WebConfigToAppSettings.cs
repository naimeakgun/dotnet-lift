using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;

namespace DotnetLift.Core.Upgrade;

/// <summary>
/// Writes appsettings.json next to Web.config from its appSettings and connection strings.
/// Web.config is not touched: the .NET Framework app keeps running until the ASP.NET Core project replaces it,
/// and a legacy project file does not pick up the new file, so this step is safe to apply early.
/// </summary>
public sealed class WebConfigToAppSettings : IStepExecutor
{
    public string StepId => "appsettings";

    public bool CanExecute(ProjectInfo project) => project.IsWeb && project.WebConfigPath is not null;

    public IReadOnlyList<FileChange> Prepare(ProjectInfo project, UpgradeStep step, string targetFramework)
    {
        if (project.WebConfigPath is not { } webConfig || !File.Exists(webConfig)
            || AppSettingsJson.ReadWebConfig(webConfig) is not { } settings)
        {
            return []; // a Web.config that is not XML is reported by the analysis
        }

        var target = Path.Combine(project.Directory, "appsettings.json");
        var existing = File.Exists(target) ? File.ReadAllText(target) : null;
        return AppSettingsJson.Render(existing, settings) is { } json ? [new FileChange(target, json)] : [];
    }
}
