# Sync Central Versions

This will automatically synchronize your packages and versions to work nicely with Microsoft.Build.CentralPackageVersions.

- Packages with versions in your projects will be moved to `Packages.props`
- Packages without versions will be automatically added to `Packages.props` (for example when an SDK contributes a package to the build pipeline)
- Packages missing from `Packages.props` will be added (the latest released version)

# Status

<!-- badges -->

[![github-release-badge]][github-release]
[![github-license-badge]][github-license]
[![codecov-badge]][codecov]

<!-- badges -->

<!-- history badges -->

| Azure Pipelines                                           |
| --------------------------------------------------------- |
| [![azurepipelines-badge]][azurepipelines]                 |
| [![azurepipelines-history-badge]][azurepipelines-history] |

<!-- history badges -->

<!-- nuget packages -->

| Package               | NuGet                                                                                          |
| --------------------- | ---------------------------------------------------------------------------------------------- |
| sync-central-versions | [![nuget-version-2booyodh5faq-badge]![nuget-downloads-2booyodh5faq-badge]][nuget-2booyodh5faq] |

<!-- nuget packages -->

# Whats next?

TBD

<!-- generated references -->

[github-release]: https://github.com/david-driscoll/sync-central-versions/releases/latest
[github-release-badge]: https://img.shields.io/github/release/david-driscoll/sync-central-versions.svg?logo=github&style=flat "Latest Release"
[github-license]: https://github.com/david-driscoll/sync-central-versions/blob/master/LICENSE
[github-license-badge]: https://img.shields.io/github/license/david-driscoll/sync-central-versions.svg?style=flat "License"
[codecov]: https://codecov.io/gh/david-driscoll/sync-central-versions
[codecov-badge]: https://img.shields.io/codecov/c/github/david-driscoll/sync-central-versions.svg?color=E03997&label=codecov&logo=codecov&logoColor=E03997&style=flat "Code Coverage"
[azurepipelines]: https://daviddriscoll.visualstudio.com/GitHub/_build/latest?definitionId=8&branchName=master
[azurepipelines-badge]: https://img.shields.io/azure-devops/build/daviddriscoll/GitHub/8.svg?color=98C6FF&label=azure%20pipelines&logo=azuredevops&logoColor=98C6FF&style=flat "Azure Pipelines Status"
[azurepipelines-history]: https://daviddriscoll.visualstudio.com/GitHub/_build?definitionId=8&branchName=master
[azurepipelines-history-badge]: https://buildstats.info/azurepipelines/chart/daviddriscoll/GitHub/8?includeBuildsFromPullRequest=false "Azure Pipelines History"
[nuget-2booyodh5faq]: https://www.nuget.org/packages/sync-central-versions/
[nuget-version-2booyodh5faq-badge]: https://img.shields.io/nuget/v/sync-central-versions.svg?color=004880&logo=nuget&style=flat-square "NuGet Version"
[nuget-downloads-2booyodh5faq-badge]: https://img.shields.io/nuget/dt/sync-central-versions.svg?color=004880&logo=nuget&style=flat-square "NuGet Downloads"

<!-- generated references -->

<!-- nuke-data
github:
  owner: david-driscoll
  repository: sync-central-versions
azurepipelines:
  account: daviddriscoll
  teamproject: GitHub
  builddefinition: 8
-->
