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
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
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

    [Command("sync", Description =
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

        [Option(CommandOptionType.SingleValue,
            Description = "Specify the file that contains your versions (usually Packages.props)", ShortName = "props")]
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

            if (string.IsNullOrWhiteSpace(PackagesProps))
            {
                PackagesProps = Path.Combine(Directory.GetCurrentDirectory(), "Packages.props");
            }

            try
            {
                if (Solution == null || !File.Exists(Solution))
                {
                    _logger.LogCritical(
                        "No solution found or provided (Solution: {Solution}, CurrentDirectory: {CurrentDirectory})",
                        Solution, Directory.GetCurrentDirectory());
                    return 1;
                }

                if (PackagesProps == null || !File.Exists(PackagesProps))
                {
                    _logger.LogCritical(
                        "No Packages.props found or provided (PackagesProps: {PackagesProps}, CurrentDirectory: {CurrentDirectory})",
                        PackagesProps, Directory.GetCurrentDirectory());
                    return 1;
                }

                static async Task<XDocument> LoadDocument(string file)
                {
                    await using var packagesFile = File.OpenRead(file);
                    return XDocument.Load(packagesFile, LoadOptions.PreserveWhitespace);
                }

                var document = await LoadDocument(PackagesProps);

                await _packageSync.AddMissingPackages(Solution, document, cts.Token).ConfigureAwait(false);
                await _packageSync.RemoveExtraPackages(Solution, document, cts.Token).ConfigureAwait(false);
                await _packageSync.MoveVersions(Solution, document, cts.Token).ConfigureAwait(false);

                await PackageSync.UpdateXDocument(PackagesProps, document, cts.Token).ConfigureAwait(false);
                {
                    document = await LoadDocument(PackagesProps);
                    await PackageSync.UpdateXDocument(PackagesProps, document, cts.Token).ConfigureAwait(false);
                }
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
        private readonly ILoggerFactory _loggerFactory;

        public PackageSync(ILogger<PackageSync> logger, ILoggerFactory loggerFactory)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        public async Task AddMissingPackages(string solutionPath, XDocument packagesProps,
            CancellationToken cancellationToken
        )
        {

            var packageReferences = packagesProps.Descendants("PackageReference")
                .Concat(packagesProps.Descendants("GlobalPackageReference"))
                .Select(x => x.Attributes().First(x => x.Name == "Include" || x.Name == "Update").Value)
                .ToArray();

            _logger.LogTrace("Found {0} existing package references", packageReferences.Length);

            var missingPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sln = GetSolution(solutionPath);
            var projects = sln.Projects.Values.SelectMany(project => project.Build()).ToImmutableArray();
            foreach (var project in projects)
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

                        _logger.LogInformation("Package {0} is missing and will be added to props file", item.ItemSpec);
                        missingPackages.Add(item.ItemSpec);
                    }
                }
            }

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

                GetItemGroupToInsertInto(packagesProps, projects.FirstOrDefault(z => z.PackageReferences.ContainsKey(item)),
                    item).Add(element);
            }

            OrderPackageReferences(packagesProps);
            RemoveDuplicatePackageReferences(packagesProps);
        }

        public async Task RemoveExtraPackages(
            string solutionPath,
            XDocument packagesProps, CancellationToken cancellationToken
        )
        {
            var packageReferences = new HashSet<string?>(packagesProps.Descendants("PackageReference")
                .Concat(packagesProps.Descendants("GlobalPackageReference"))
                .Select(x => x.Attributes().First(x => x.Name == "Include" || x.Name == "Update").Value)
                .ToList());

            foreach (var project in GetSolution(solutionPath).Projects.Values.SelectMany(project => project.Build()))
            {
                if (!project.Items.TryGetValue("PackageReference", out var projectPackageReferences)) continue;
                foreach (var item in projectPackageReferences)
                {
                    packageReferences.Remove(item.ItemSpec);
                }
            }

            if (packageReferences.Count > 0)
            {
                foreach (var package in packageReferences)
                {
                    foreach (var item in packagesProps.Descendants("PackageReference")
                        .Concat(
                            packagesProps.Descendants("GlobalPackageReference")
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
        }

        public async Task MoveVersions(
            string solutionPath,
            XDocument packagesDocument, CancellationToken cancellationToken
        )
        {
            var projects = GetSolution(solutionPath).Projects.Values.Select(x => x)
                .SelectMany(
                    projectReference => projectReference.Build().Select(project =>
                    {
                        using var file = File.OpenRead(projectReference.ProjectFile.Path);
                        return (path: projectReference.ProjectFile.Path,
                            document: XDocument.Load(file, LoadOptions.PreserveWhitespace), project);
                    }));
            foreach (var (path, document, project) in projects)
            {
                foreach (var item in document.Descendants("PackageReference")
                    .Where(x => !string.IsNullOrEmpty(x.Attribute("Version")?.Value))
                    .ToArray()
                )
                {
                    _logger.LogInformation(
                        "Found Version {Version} on {Package} in {Path} and moving it to props file",
                        item.Attribute("Version").Value,
                        item.Attribute("Include").Value,
                        path
                    );
                    var @new = new XElement(item);
                    @new.SetAttributeValue("Update", @new.Attribute("Include").Value);
                    @new.SetAttributeValue("Include", null);
                    @new.SetAttributeValue("Version", null);
                    @new.SetAttributeValue("Version", item.Attribute("Version").Value);
                    var itemGroupToInsertInto =
                        GetItemGroupToInsertInto(document, project, @new.Attribute("Update").Value);
                    foreach (var an in itemGroupToInsertInto.Descendants("PackageReference").LastOrDefault()
                                           ?.Annotations<XmlSignificantWhitespace>() ??
                                       Enumerable.Empty<XmlSignificantWhitespace>())
                    {
                        @new.AddAnnotation(an);
                    }

                    itemGroupToInsertInto.Add(@new);
                    item.SetAttributeValue("Version", null);
                }

                await UpdateXDocument(path, document, cancellationToken).ConfigureAwait(false);
            }

            OrderPackageReferences(packagesDocument);
            RemoveDuplicatePackageReferences(packagesDocument);
        }

        static XElement GetItemGroupToInsertInto(XDocument document, AnalyzerResult? project, string packageName)
        {
            var itemGroups = document.Descendants("ItemGroup").ToImmutableArray();
            if (itemGroups.Count() <= 3)
            {
                var projectElement = document.Descendants("Project").Single();
                for (var i = 0; i < 4 - itemGroups.Count(); i++)
                {
                    projectElement.Add(new XElement("ItemGroup"));
                }

                itemGroups = document.Descendants("ItemGroup").ToImmutableArray();
                // itemGroups[0].SetAttributeValue("Kind", "Global Package References");
                // itemGroups[1].SetAttributeValue("Kind", "Build Package References");
                // itemGroups[2].SetAttributeValue("Kind", "Package References");
                // itemGroups[3].SetAttributeValue("Kind", "Test Package References");
            }

            if (project?.PackageReferences.TryGetValue("Nuke.Common", out _) == true)
            {
                return itemGroups.Skip(1).First();
            }

            if (project?.PackageReferences.TryGetValue("xunit", out _) == true && !packageName.StartsWith("System."))
            {
                return itemGroups.Last();
            }

            return itemGroups.Skip(2).First();
        }

        private AnalyzerManager GetSolution(string solutionPath) => new AnalyzerManager(solutionPath,
            new AnalyzerManagerOptions() {LoggerFactory = _loggerFactory});

        public static async Task UpdateXDocument(string path, XDocument document, CancellationToken cancellationToken)
        {
            await using var fileWrite = File.Open(path, FileMode.Truncate);
            using var writer = XmlWriter.Create(
                fileWrite,
                new XmlWriterSettings {OmitXmlDeclaration = true, Async = true, Indent = true}
            );
            await document.SaveAsync(writer, cancellationToken).ConfigureAwait(false);
        }

        private static void OrderPackageReferences(XDocument document)
        {
            foreach (var itemGroup in document.Descendants("ItemGroup"))
            {
                foreach (var kind in new[] {"GlobalPackageReference", "PackageReference"})
                {
                    var toSort = itemGroup.Descendants(kind).ToArray();
                    var sorted = itemGroup.Descendants(kind).Select(x => new XElement(x))
                        .OrderBy(x => x.Attribute("Include")?.Value ?? x.Attribute("Update")?.Value).ToArray();
                    for (var i = 0; i < sorted.Length; i++)
                    {
                        toSort[i].ReplaceAttributes(sorted[i].Attributes());
                    }
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
