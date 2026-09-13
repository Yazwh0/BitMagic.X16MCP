using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using X16M.Dap;

namespace X16M;

internal sealed class Options
{
    [Option("x16d", Default = "", Required = false, HelpText = "Path to X16D.exe to spawn. Falls back to the X16D_PATH environment variable.")]
    public string X16DPath { get; set; } = "";

    [Option("x16d-host", Default = "", Required = false, HelpText = "Host of an X16D already running with --dapport, to connect to instead of spawning one. Falls back to the X16D_HOST environment variable.")]
    public string X16DHost { get; set; } = "";

    [Option("x16d-port", Default = 0, Required = false, HelpText = "Port of an X16D already running with --dapport. Falls back to the X16D_PORT environment variable.")]
    public int X16DPort { get; set; }
}

internal static class Program
{
    private const string X16DPathEnvironmentVariable = "X16D_PATH";
    private const string X16DHostEnvironmentVariable = "X16D_HOST";
    private const string X16DPortEnvironmentVariable = "X16D_PORT";

    private static async Task<int> Main(string[] args)
    {
        var options = Parser.Default.ParseArguments<Options>(args).Value ?? new Options();

        X16DConnection? connection = ResolveConnection(options, out var resolutionError);
        if (connection is null)
        {
            await Console.Error.WriteLineAsync(resolutionError);
            return 1;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Our stdout is the MCP JSON-RPC channel to the client - console logging must go to
        // stderr only, never stdout, or it corrupts the protocol stream.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(new DapSession(connection));

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }

    private static X16DConnection? ResolveConnection(Options options, out string error)
    {
        error = "";

        var host = !string.IsNullOrWhiteSpace(options.X16DHost)
            ? options.X16DHost
            : Environment.GetEnvironmentVariable(X16DHostEnvironmentVariable) ?? "";

        var port = options.X16DPort != 0
            ? options.X16DPort
            : int.TryParse(Environment.GetEnvironmentVariable(X16DPortEnvironmentVariable), out var envPort) ? envPort : 0;

        if (!string.IsNullOrWhiteSpace(host) || port != 0)
        {
            if (string.IsNullOrWhiteSpace(host) || port == 0)
            {
                error = "X16M: --x16d-host/X16D_HOST and --x16d-port/X16D_PORT must both be set to connect over TCP.";
                return null;
            }

            return new X16DConnection.Tcp(host, port);
        }

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var bundledX16DPath = Path.Combine(exeDir, "x16d", "X16D.exe");

        var x16dPath = !string.IsNullOrWhiteSpace(options.X16DPath)
            ? options.X16DPath
            : Environment.GetEnvironmentVariable(X16DPathEnvironmentVariable)
                ?? (File.Exists(bundledX16DPath) ? bundledX16DPath : "");

        if (string.IsNullOrWhiteSpace(x16dPath))
        {
            error =
                $"X16M: no X16D configured. Expected a bundled copy at '{bundledX16DPath}', or pass --x16d <path-to-X16D.exe> " +
                $"(or set {X16DPathEnvironmentVariable}) to spawn one, or --x16d-host/--x16d-port " +
                $"(or {X16DHostEnvironmentVariable}/{X16DPortEnvironmentVariable}) to connect to one already running with --dapport.";
            return null;
        }

        return new X16DConnection.Spawn(x16dPath);
    }
}
