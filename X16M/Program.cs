using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using X16M.Dap;

namespace X16M;

internal sealed class Options
{
    [Option("x16d", Default = "", Required = false, HelpText = "Path to an X16D executable to spawn instead of the one bundled with X16M. Falls back to the X16D_PATH environment variable, then the bundled X16D.")]
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

        // A null connection with no error means "nothing configured" - fine, attach_to_session
        // doesn't need one. A null connection WITH an error means the caller got something
        // wrong (e.g. only one of --x16d-host/--x16d-port) and should be told, not silently
        // dropped into attach-only mode.
        X16DConnection? connection = ResolveConnection(options, out var resolutionError);
        if (connection is null && resolutionError is not null)
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
            .AddMcpServer(o => o.ServerInstructions =
                "No need to call attach_to_session or launch_project up front: every other tool " +
                "here attaches automatically on first use if VSCode already has a debug session " +
                "running (via its 'Run debug sessions through the same background process' " +
                "setting) - just call get_stack_trace/get_sprites/read_memory/etc. directly. Two " +
                "ways a session ends up active: (1) launch_project starts and owns one yourself - " +
                "step/continue/breakpoints/disconnect are all yours to drive. (2) auto-attach (or " +
                "an explicit attach_to_session) joins a session VSCode already launched and owns - " +
                "stepping, breakpoints, continue, and disconnect stay with VSCode (or its own chat " +
                "integration); this server's tools there are only for reading and amending " +
                "X16-specific state (memory, sprites, palette, layers, CPU history, taking snapshots), not for " +
                "driving execution - those calls fail with a clear error explaining that. The two " +
                "modes are mutually exclusive per session - call disconnect before switching. " +
                "build_project is the one exception to all of the above: it just compiles a " +
                "project or a single .bmasm/.asm file and reports errors, with no session, ROM, " +
                "or emulator involved at all - use it as a fast compile-check loop while writing " +
                "bmasm, independent of whatever launch_project/attach_to_session state exists.")
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }

    private static X16DConnection? ResolveConnection(Options options, out string? error)
    {
        error = null;

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

        var envX16DPath = Environment.GetEnvironmentVariable(X16DPathEnvironmentVariable);

        var x16dPath = !string.IsNullOrWhiteSpace(options.X16DPath) ? options.X16DPath
            : !string.IsNullOrWhiteSpace(envX16DPath) ? envX16DPath
            : FindBundledX16D() ?? "";

        if (string.IsNullOrWhiteSpace(x16dPath))
        {
            // Not an error: no default connection just means launch_project won't work until
            // one is configured, but attach_to_session (which needs none) still will.
            return null;
        }

        return new X16DConnection.Spawn(x16dPath);
    }

    // The X16D that ships with X16M, in x16d/ next to it: the release archives and the
    // per-RID .NET tool packages both put it there. AppContext.BaseDirectory rather than
    // the process path, as that's dotnet itself when run as 'dotnet X16M.dll'.
    private static string? FindBundledX16D()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "x16d", OperatingSystem.IsWindows() ? "X16D.exe" : "X16D");
        if (!File.Exists(path))
            return null;

        // NuGet doesn't keep Unix file permissions, so in the Linux tool package X16D arrives
        // without its executable bit. Put it back, or spawning it fails with 'permission denied'.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((mode & UnixFileMode.UserExecute) == 0)
                    File.SetUnixFileMode(path, mode | execute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"X16M: couldn't make the bundled X16D executable ({ex.Message}). Run: chmod +x \"{path}\"");
            }
        }

        return path;
    }
}
