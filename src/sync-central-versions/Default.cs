using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Rocket.Surgery.Extensions.CommandLine;

namespace sync_central_versions
{
    class Default : IDefaultCommandAsync
    {
        private readonly IApplicationState _state;
        private readonly PackageSync _packageSync;
        private readonly ILogger<Sync> _logger;

        public Default(IApplicationState state, PackageSync packageSync, ILogger<Sync> logger)
        {
            _state = state;
            _packageSync = packageSync;
            _logger = logger;
        }

        [Option(CommandOptionType.SingleValue, Description = "Specify the solution to process", ShortName = "sln")]
        public string Solution { get; set; }

        [Option(CommandOptionType.SingleValue,
            Description = "Specify the file that contains your versions (usually Packages.props)", ShortName = "props")]
        public string PackagesProps { get; set; }

        public Task<int> Run(IApplicationState state, CancellationToken cancellationToken) =>
            new Sync(_packageSync, _logger) {Solution = Solution, PackagesProps = PackagesProps}
                .OnExecuteAsync();
    }
}
