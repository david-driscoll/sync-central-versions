using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Hosting;
using sync_central_versions;
using Rocket.Surgery.Conventions;
using Rocket.Surgery.Extensions.CommandLine;
using Rocket.Surgery.Extensions.DependencyInjection;
using Rocket.Surgery.Hosting;

[assembly: Convention(typeof(Program))]

namespace sync_central_versions
{
    [PublicAPI]
    public class Program : ICommandLineConvention, IServiceConvention
    {
        public static Task<int> Main(string[] args) => CreateHostBuilder(args).RunCli();

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .LaunchWith(RocketBooster.For(DependencyContext.Default));

        public void Register(ICommandLineConventionContext context)
        {
            context.OnRunAsync<Default>();
            context.AddCommand<Sync>();
        }

        public void Register(IServiceConventionContext context)
        {
            context.Services.AddSingleton<PackageSync>();
        }
    }
}
