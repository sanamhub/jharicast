using System.Threading.Tasks;

namespace Jharicast.Cli;

internal static class Program
{
    public static Task<int> Main(string[] args) => Cli.RunAsync(args, CliHost.Console());
}
