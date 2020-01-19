using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;

namespace sync_central_versions
{
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
        public async Task<int> OnExecuteAsync()
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
                
                _logger.LogInformation("Processing solution {Solution}, using {PackagesProps} as the version props file.", Solution, PackagesProps);
                
                _logger.LogInformation("Looking for missing packages...");
                var missingPackages = await _packageSync.AddMissingPackages(Solution, document, cts.Token).ConfigureAwait(false);
                if (missingPackages.Any())
                _logger.LogInformation("Added the following missing package references: {@Packages}", missingPackages);
                
                _logger.LogInformation("Looking for versions in project files...");
                var movedVersions = await _packageSync.MoveVersions(Solution, document, cts.Token).ConfigureAwait(false);
                
                _logger.LogInformation("Looking for extra packages that are no longer referenced...");
                var extraPackages = await _packageSync.RemoveExtraPackages(Solution, document, cts.Token).ConfigureAwait(false);
                if (extraPackages.Any())
                _logger.LogInformation("Removed the following extra package references: {@Packages}", extraPackages);

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
}
