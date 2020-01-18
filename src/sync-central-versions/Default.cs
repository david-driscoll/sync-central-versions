using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Buildalyzer;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
using sync_central_versions;
using Rocket.Surgery.Conventions;
using Rocket.Surgery.Extensions.CommandLine;

namespace sync_central_versions
{
    class Default : IDefaultCommandAsync
    {
        private readonly IApplicationState _state;
        private readonly ILogger<Default> _logger;

        public Default(IApplicationState state, ILogger<Default> logger)
        {
            _state = state;
            _logger = logger;
        }

        public Task<int> Run(IApplicationState state, CancellationToken cancellationToken)
        {
            _logger.LogWarning("Please provide a command to run!");
            return Task.FromResult(1);
        }
    }

    [Command(Description =
        "Synchronize all the packages with the projects in the given solution.  This will add any packages that might be missing (as defined by sdks or external references).  It will remove any packages that are no longer referenced.  And also move all versions from packages into the give packages props.")]
    class Sync
    {
        private readonly PackageSync _packageSync;
        private readonly ILogger<Sync> _logger;

        public Sync(PackageSync packageSync, ILogger<Sync> logger)
        {
            _packageSync = packageSync;
            _logger = logger;
        }

        [Option(CommandOptionType.SingleValue, Description = "Specify the solution to process", ShortName = "sln")]
        public string Solution { get; set; }

        [Option(CommandOptionType.SingleValue, Description = "Specify the file that contains your versions (usually Packages.props)", ShortName = "props")]
        public string PackagesProps { get; set; }

        [UsedImplicitly]
        private async Task<int> OnExecuteAsync()
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(10));

            if (string.IsNullOrWhiteSpace(Solution))
            {
                Solution = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.sln").FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(Solution))
            {
                PackagesProps = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "Packages.props")
                    .FirstOrDefault();
            }

            try
            {
                if (Solution == null)
                {
                    _logger.LogCritical("No solution found or provided");
                    return 1;
                }
                if (PackagesProps == null)
                {
                    _logger.LogCritical("No Packages.props found or provided");
                    return 1;
                }

                await _packageSync.AddMissingPackages(Solution, PackagesProps, cts.Token).ConfigureAwait(false);
                await _packageSync.RemoveExtraPackages(Solution, PackagesProps, cts.Token).ConfigureAwait(false);
                await _packageSync.MoveVersions(Solution, PackagesProps, cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException tce)
            {
                _logger.LogCritical("Operation did not complete after 10 minutes");
                return 1;
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "There was an unhandled exception...");
                return 1;
            }

            return 0;
        }
    }


    class PackageSync
    {
        private ILogger<PackageSync> _logger;

        public PackageSync(ILogger<PackageSync> logger) => _logger = logger;

        public async Task AddMissingPackages(string solutionPath, string packagesProps,
            CancellationToken cancellationToken
        )
        {
            XDocument document;
            {
                await using var packagesFile = File.OpenRead(packagesProps);
                document = XDocument.Load(packagesFile, LoadOptions.PreserveWhitespace);
            }

            var packageReferences = document.Descendants("PackageReference")
                .Concat(document.Descendants("GlobalPackageReference"))
                .Select(x => x.Attributes().First(x => x.Name == "Include" || x.Name == "Update").Value)
                .ToArray();

            _logger.LogTrace("Found {0} existing package references", packageReferences.Length);

            var missingPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in GetProjects(solutionPath).SelectMany(project => project.Build()))
            {
                if (project.Items.TryGetValue("PackageReference", out var projectPackageReferences))
                {
                    foreach (var item in projectPackageReferences
                        .Where(x => !x.Metadata.ContainsKey("IsImplicitlyDefined") &&
                                    !x.Metadata.ContainsKey("Version"))
                    )
                    {
                        if (packageReferences.Any(z => z.Equals(item.ItemSpec, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        _logger.LogInformation("Package {0} is missing and will be added to {1}", item.ItemSpec,
                            packagesProps);
                        missingPackages.Add(item.ItemSpec);
                    }
                }
            }

            var itemGroups = document.Descendants("ItemGroup").ToImmutableArray();
            var itemGroupToInsertInto = itemGroups.Count() > 2 ? itemGroups.Skip(1).Take(1).First() : itemGroups.Last();

            var providers = new List<Lazy<INuGetResourceProvider>>();
            providers.AddRange(Repository.Provider.GetCoreV3()); // Add v3 API support
            var packageSource = new PackageSource("https://api.nuget.org/v3/index.json");
            var sourceRepository = new SourceRepository(packageSource, providers);
            using var sourceCacheContext = new SourceCacheContext();

            foreach (var item in missingPackages.OrderBy(x => x))
            {
                var element = new XElement("PackageReference");
                element.SetAttributeValue("Update", item);
                {
                    var dependencyInfoResource = await sourceRepository.GetResourceAsync<DependencyInfoResource>()
                        .ConfigureAwait(false);
                    var resolvedPackages = await dependencyInfoResource.ResolvePackages(
                        item,
                        sourceCacheContext,
                        NuGet.Common.NullLogger.Instance,
                        cancellationToken
                    ).ConfigureAwait(false);
                    var packageInfo = resolvedPackages.OrderByDescending(x => x.Identity.Version)
                        .First(x => !x.Identity.Version.IsPrerelease);
                    element.SetAttributeValue("Version", packageInfo.Identity.Version.ToString());
                    _logger.LogTrace(
                        "Found Version {0} for {1}",
                        packageInfo.Identity.Version.ToString(),
                        packageInfo.Identity.Id
                    );
                }
                itemGroupToInsertInto.Add(element);
            }

            OrderPackageReferences(itemGroups.ToArray());
            RemoveDuplicatePackageReferences(document);

            await UpdateXDocument(packagesProps, document, cancellationToken).ConfigureAwait(false);
        }

        public async Task RemoveExtraPackages(
            string solutionPath,
            string packagesProps, CancellationToken cancellationToken
        )
        {
            XDocument document;
            {
                using var packagesFile = File.OpenRead(packagesProps);
                document = XDocument.Load(packagesFile, LoadOptions.PreserveWhitespace);
            }
            var packageReferences = document.Descendants("PackageReference")
                .Concat(document.Descendants("GlobalPackageReference"))
                .Select(x => x.Attributes().First(x => x.Name == "Include" || x.Name == "Update").Value)
                .ToList();

            foreach (var project in GetProjects(solutionPath).SelectMany(project => project.Build()))
            {
                if (project.Items.TryGetValue("PackageReference", out var projectPackageReferences))
                {
                    foreach (var item in projectPackageReferences)
                    {
                        packageReferences.Remove(item.ItemSpec);
                    }
                }
            }

            if (packageReferences.Count > 0)
            {
                foreach (var package in packageReferences)
                {
                    foreach (var item in document.Descendants("PackageReference")
                        .Concat(
                            document.Descendants("GlobalPackageReference")
                                .Where(
                                    x => x.Attributes().First(x => x.Name == "Include" || x.Name == "Update").Value
                                        .Equals(package, StringComparison.OrdinalIgnoreCase)
                                )
                        )
                        .ToArray())
                    {
                        _logger.LogInformation(
                            "Removing extra PackageReference for {0}",
                            item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value
                        );
                        item.Remove();
                    }
                }
            }

            await UpdateXDocument(packagesProps, document, cancellationToken).ConfigureAwait(false);
        }

        public async Task MoveVersions(
            string solutionPath,
            string packagesProps, CancellationToken cancellationToken
        )
        {
            XDocument packagesDocument;
            {
                using var packagesFile = File.OpenRead(packagesProps);
                packagesDocument = XDocument.Load(packagesFile, LoadOptions.None);
            }

            var itemGroups = packagesDocument.Descendants("ItemGroup");
            var itemGroupToInsertInto = itemGroups.Count() > 2 ? itemGroups.Skip(1).Take(1).First() : itemGroups.Last();

            var projects = GetProjects(solutionPath).Select(x => x.ProjectFile.Path)
                .Select(
                    path =>
                    {
                        using var file = File.OpenRead(path);
                        return (path, document: XDocument.Load(file, LoadOptions.PreserveWhitespace));
                    }
                );
            foreach (var (path, document) in projects)
            {
                foreach (var item in document.Descendants("PackageReference")
                    .Where(x => !string.IsNullOrEmpty(x.Attribute("Version")?.Value))
                    .ToArray()
                )
                {
                    _logger.LogInformation(
                        "Found Version {Version} on {Package} in {Path} and moving it to {NewLocation}",
                        item.Attribute("Version").Value,
                        item.Attribute("Include").Value,
                        path,
                        packagesProps
                    );
                    var @new = new XElement(item);
                    @new.SetAttributeValue("Update", @new.Attribute("Include").Value);
                    @new.SetAttributeValue("Include", null);
                    @new.SetAttributeValue("Version", null);
                    @new.SetAttributeValue("Version", item.Attribute("Version").Value);
                    foreach (var an in itemGroupToInsertInto.Descendants("PackageReference").Last()
                        .Annotations<XmlSignificantWhitespace>())
                    {
                        @new.AddAnnotation(an);
                    }

                    itemGroupToInsertInto.Add(@new);
                    item.SetAttributeValue("Version", null);
                }

                await UpdateXDocument(path, document, cancellationToken).ConfigureAwait(false);
            }

            OrderPackageReferences(itemGroups.ToArray());
            RemoveDuplicatePackageReferences(packagesDocument);

            await UpdateXDocument(packagesProps, packagesDocument, cancellationToken).ConfigureAwait(false);
        }

        private static IEnumerable<ProjectAnalyzer> GetProjects(string solutionPath)
        {
            var am = new AnalyzerManager(solutionPath, new AnalyzerManagerOptions());
            foreach (var project in am.Projects.Values.Where(
                x => !x.ProjectFile.Path.EndsWith(".build.csproj", StringComparison.OrdinalIgnoreCase)
            ))
            {
                yield return project;
            }
        }

        private static async Task UpdateXDocument(string path, XDocument document, CancellationToken cancellationToken)
        {
            await using var fileWrite = File.Open(path, FileMode.Truncate);
            using var writer = XmlWriter.Create(
                fileWrite,
                new XmlWriterSettings {OmitXmlDeclaration = true, Async = true, Indent = true}
            );
            await document.SaveAsync(writer, cancellationToken).ConfigureAwait(false);
        }

        private static void OrderPackageReferences(params XElement[] itemGroups)
        {
            foreach (var itemGroup in itemGroups)
            {
                var toSort = itemGroup.Descendants("PackageReference").ToArray();
                var sorted = itemGroup.Descendants("PackageReference").Select(x => new XElement(x))
                    .OrderBy(x => x.Attribute("Include")?.Value ?? x.Attribute("Update")?.Value).ToArray();
                for (var i = 0; i < sorted.Length; i++)
                {
                    toSort[i].ReplaceAttributes(sorted[i].Attributes());
                }
            }
        }

        private static void RemoveDuplicatePackageReferences(XDocument document)
        {
            var packageReferences = document.Descendants("PackageReference")
                .ToLookup(x => x.Attribute("Include")?.Value ?? x.Attribute("Update")?.Value);
            foreach (var item in packageReferences.Where(item => item.Count() > 1))
            {
                item.Last().Remove();
            }
        }
    }
}
