using System.Linq;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Rocket.Surgery.Nuke.DotNetCore;
using Rocket.Surgery.Nuke;
using Nuke.Common.Tools;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Solution : DotNetCoreBuild, IDotNetCoreBuild
{
    /// <summary>
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    /// </summary>

    public static int Main() => Execute<Solution>(x => x.Default);

    Target Default => _ => _
        .DependsOn(Restore)
        .DependsOn(Build)
        .DependsOn(Test)
        .DependsOn(Pack)
        .DependsOn(Install)
        ;

    public new Target Restore => _ => _.With(this, DotNetCoreBuild.Restore);

    public new Target Build => _ => _.With(this, DotNetCoreBuild.Build);

    public new Target Test => _ => _.With(this, DotNetCoreBuild.Test);

    public new Target Pack => _ => _.With(this, DotNetCoreBuild.Pack);

    public Target Install => _ => _
        .After(Pack)
        .OnlyWhenStatic(() => IsLocalBuild)
        .Executes(() =>
        {
            try
            {
                DotNetToolUninstall(x => x.EnableGlobal().SetPackageName("sync-central-versions")
                    .ResetVerbosity());
            } catch {}

            DotNetToolInstall(x =>
                x.EnableGlobal().SetVersion(GitVersion.SemVer).AddSources(NuGetPackageDirectory)
                    .SetPackageName("sync-central-versions"));
        });
}
