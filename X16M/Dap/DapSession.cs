using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using ModelContextProtocol;
using Newtonsoft.Json.Linq;
using X16M.Rom;
using Thread = System.Threading.Thread;

namespace X16M.Dap;

/// <summary>
/// Outcome of waiting for the target to stop after a continue/step/launch request.
/// <see cref="Terminated"/> false and <see cref="Stopped"/> null together mean "still running,
/// no stop observed within the wait window" - only possible from <see cref="DapSession.Launch"/>,
/// whose initial wait is short and non-fatal on timeout (some targets never produce an early
/// stop at all, and an agent still needs a chance to set breakpoints promptly either way).
/// </summary>
public sealed record StopOutcome(bool Terminated, StoppedEvent? Stopped)
{
    public bool StillRunning => !Terminated && Stopped is null;
}

/// <summary>One file's worth of breakpoints to request, e.g. as part of <see cref="DapSession.Launch"/>.</summary>
public sealed record BreakpointSpec(string File, IReadOnlyList<int> Lines);

public enum BreakpointKind { Source, Instruction, Hardware }

/// <summary>A breakpoint as last reported by X16D. <see cref="File"/> is set for source breakpoints.</summary>
public sealed record KnownBreakpoint(BreakpointKind Kind, string? File, Breakpoint Breakpoint);

/// <summary>Result of <see cref="DapSession.Launch"/>, plus a best-effort warning if no ROM looked reachable.</summary>
public sealed record LaunchOutcome(StopOutcome Stop, IReadOnlyList<Breakpoint> InitialBreakpoints, string? RomWarning);

/// <summary>
/// Speaks DAP to X16D via <see cref="DebugProtocolHost"/>, exactly as VS Code would - either by
/// spawning it directly, or by connecting to one already running with `--dapport` (so it can be
/// started separately, e.g. under the Visual Studio debugger). One session at a time; call
/// <see cref="Disconnect"/> before a new <see cref="Launch"/>.
/// </summary>
public sealed class DapSession : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Deliberately short and non-fatal: some targets never produce an early stop at all (no
    // StartStepping-style pause), and a caller still needs launch_project to return promptly so
    // it can set breakpoints before the target runs further - not block for up to 30s only to
    // then get an error instead of a usable session.
    private static readonly TimeSpan LaunchInitialStopTimeout = TimeSpan.FromSeconds(3);

    // Null when X16M was started with no --x16d/--x16d-host configured - Launch() then isn't
    // usable (there's nothing to spawn or connect to), but Attach() never needs this at all.
    private readonly X16DConnection? _connection;
    private readonly object _gate = new();

    // Serializes EnsureSessionAsync's own check-then-attach: MCP can dispatch a batch of tool
    // calls concurrently, and without this, several calls that all observe IsActive == false at
    // once would all race into Attach() - only the first to reach Attach's own lock actually
    // attaches, and every other racer hits Attach's "already running" guard, even though none of
    // them ever called attach_to_session themselves. Re-checking IsActive after acquiring this
    // turns those losers into no-ops instead of spurious errors.
    private readonly SemaphoreSlim _ensureSessionLock = new(1, 1);

    // Serializes continue/step calls: DAP only supports one outstanding execution-control
    // operation at a time. Without this, two overlapping calls (MCP can dispatch tool calls
    // concurrently) would both write _pendingStop, the second clobbering the first's
    // TaskCompletionSource - the target then receives more continue/step requests than the
    // caller accounted for, and the first call's result is silently lost.
    private readonly SemaphoreSlim _stepLock = new(1, 1);

    private Process? _process;
    private TcpClient? _tcpClient;
    private DebugProtocolHost? _host;
    private Thread? _pumpThread;
    private TaskCompletionSource<StopOutcome>? _pendingStop;
    private bool _active;
    private string? _projectDirectory;

    // Last error-severity OutputEvent from X16D, e.g. "*** Rom file not found: rom.bin".
    private string? _lastErrorOutput;

    // Every breakpoint set this session, grouped the way the set_* calls replace them: source
    // breakpoints per file, plus one group each for instruction and hardware breakpoints. So a
    // set_* call drops the ones it replaces, rather than leaving them listed forever. Kept current
    // by OnBreakpointEvent (matched by id), so a caller can see whether a breakpoint has since
    // become verified without re-sending it. Locked, as events arrive on the DAP thread.
    private readonly Dictionary<string, List<Breakpoint>> _breakpointGroups = new();
    private const string InstructionGroup = "instruction";
    private const string HardwareGroup = "hardware";
    private const string FileGroupPrefix = "file:";

    // A live MCP session's stderr isn't something a user can easily get to, so DAP traffic is
    // also written to a log file next to the executable (not the process's working directory,
    // which callers don't control) - overwritten fresh on each Launch.
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "x16m-dap.log");
    private StreamWriter? _logWriter;

    public DapSession(X16DConnection? connection)
    {
        _connection = connection;
    }

    public bool IsActive => _active;
    public bool IsAttached { get; private set; }
    public StoppedEvent? LastStop { get; private set; }
    public bool Terminated { get; private set; }

    private const string ConnectionInfoFileName = "bitmagic-debug-session.json";

    // Written by BitMagic.VSC next to the project whenever its shared debug process (re)starts -
    // see extension.ts. Checked one directory at a time up to two levels, since this process's
    // own cwd (set by whatever launched it) may be a subfolder of the project root.
    internal static (string host, int port)? FindConnectionInfo(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        for (var depth = 0; depth < 2 && dir is not null; depth++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".vscode", ConnectionInfoFileName);
            if (!File.Exists(path))
                continue;

            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                var host = json.Value<string>("host");
                var port = json.Value<int?>("queryPort");
                if (!string.IsNullOrWhiteSpace(host) && port is > 0)
                    return (host!, port.Value);
            }
            catch
            {
                // Malformed/mid-write file - keep looking rather than fail outright.
            }
        }

        return null;
    }

    // So callers don't have to remember to call attach_to_session first: every read/amend method
    // below calls this instead of the old plain RequireActive(). No-op once a session (launched
    // or attached) is already active; only tries to attach when idle, never when something's
    // already in progress or already owned.
    private async Task EnsureSessionAsync()
    {
        if (IsActive)
            return;

        await _ensureSessionLock.WaitAsync();
        try
        {
            // Someone else's EnsureSessionAsync may have already attached while we waited.
            if (IsActive)
                return;

            var found = FindConnectionInfo(Directory.GetCurrentDirectory());
            if (found is null)
                throw new McpException("No active debug session. Call launch_project to start one, or make sure VSCode has a debug session running.");

            await Attach(found.Value.host, found.Value.port);
        }
        finally
        {
            _ensureSessionLock.Release();
        }
    }

    // Attaches to a session someone else (VSCode) already launched and owns, e.g. on X16D's
    // --queryport. Unlike Launch, this never creates a session - only views/amends one that's
    // already live - so it skips the ROM check, launch request, and initial breakpoints.
    public async Task Attach(string host, int port)
    {
        lock (_gate)
        {
            if (IsActive)
                throw new McpException("A debug session is already running. Call disconnect first.");

            _logWriter?.Dispose();
            _logWriter = new StreamWriter(LogPath, append: true) { AutoFlush = true };
            Log($"=== Attach: {host}:{port} ===");

            _tcpClient = new TcpClient();
            _tcpClient.Connect(host, port);
            var stream = _tcpClient.GetStream();

            _host = new DebugProtocolHost(stream, stream, registerStandardHandlers: false);
            _host.LogMessage += (_, e) => Log($"[DAP:{e.Category}] {e.Message}");
            _host.RegisterEventType<StoppedEvent>(OnStopped);
            _host.RegisterEventType<TerminatedEvent>(OnTerminated);
            _host.RegisterEventType<ExitedEvent>(_ => OnTerminated(new TerminatedEvent()));
            _host.RegisterEventType<OutputEvent>(e =>
            {
                Log($"[X16D:{e.Category}] {e.Output?.TrimEnd()}");
                if (e.Severity == OutputEvent.SeverityValue.Error && !string.IsNullOrWhiteSpace(e.Output))
                    _lastErrorOutput = e.Output.TrimEnd();
            });
            _host.RegisterEventType<BreakpointEvent>(OnBreakpointEvent);

            _pumpThread = new Thread(() =>
            {
                _host.Run();
                _host.WaitForReader();
            })
            {
                IsBackground = true,
                Name = "X16M DAP pump (attach)",
            };
            _pumpThread.Start();

            _active = true;
            IsAttached = true;
            Terminated = false;
            LastStop = null;
            _lastErrorOutput = null;
            ClearKnownBreakpoints();

            _host.SendRequestSync(new InitializeRequest("x16m"));
        }

        try
        {
            await Task.Run(() => _host!.SendRequestSync(new AttachRequest()));
        }
        catch (Exception ex)
        {
            var detail = _lastErrorOutput ?? ex.Message;
            Shutdown();
            throw new McpException($"Could not attach: {detail}");
        }
    }

    public async Task<LaunchOutcome> Launch(string projectPath, IReadOnlyList<BreakpointSpec>? initialBreakpoints = null, string? workingDirectory = null, bool stopOnEntry = true, bool muteAudio = true, string? workspaceFolder = null)
    {
        var launchCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialBreakpointResults = new List<Breakpoint>();
        string? romWarning;

        lock (_gate)
        {
            if (IsActive)
                throw new McpException("A debug session is already running. Call disconnect first.");

            if (_connection is null)
                throw new McpException("X16M has no X16D to launch: the one bundled with it wasn't found, and neither --x16d/X16D_PATH nor --x16d-host/--x16d-port was given. Reinstall X16M, or restart it with --x16d pointing at an X16D executable. attach_to_session still works without one.");

            if (!File.Exists(projectPath))
                throw new McpException($"Project file not found at '{projectPath}'.");

            // Best-effort only (doesn't know project.json-level RomFile/EmulatorDirectory
            // overrides), so a miss is a warning, not a launch precondition.
            romWarning = _connection is X16DConnection.Spawn romCheckSpawn
                ? CheckRomWarning(romCheckSpawn.ExecutablePath)
                : null;

            _logWriter?.Dispose();
            // Appended, not truncated: a live testing session often launches more than once
            // (retrying after a failure), and overwriting on each Launch was destroying the
            // very history needed to diagnose the previous attempt.
            _logWriter = new StreamWriter(LogPath, append: true) { AutoFlush = true };
            Log($"=== Launch: {projectPath} ===");

            var (input, output) = _connection switch
            {
                X16DConnection.Spawn spawn => StartProcess(spawn, workingDirectory),
                X16DConnection.Tcp tcp => ConnectTcp(tcp),
                _ => throw new McpException($"Unknown X16D connection type '{_connection.GetType()}'."),
            };

            // registerStandardHandlers defaults to true on the 2-arg ctor, which pre-registers
            // handlers for standard events like "stopped" - that collides with our own
            // RegisterEventType calls below, so register nothing by default and handle
            // everything ourselves.
            _host = new DebugProtocolHost(input, output, registerStandardHandlers: false);

            // Raw DAP wire traffic, both directions - the same diagnostic hook X16Debug.cs uses
            // on itself (its own #if SHOWDAP block). A live MCP session's stderr isn't visible
            // to a user, so this goes to LogPath (see its declaration) instead/as well.
            _host.LogMessage += (_, e) => Log($"[DAP:{e.Category}] {e.Message}");

            _host.RegisterEventType<StoppedEvent>(OnStopped);
            _host.RegisterEventType<TerminatedEvent>(OnTerminated);
            _host.RegisterEventType<ExitedEvent>(_ => OnTerminated(new TerminatedEvent()));
            _host.RegisterEventType<OutputEvent>(e =>
            {
                Log($"[X16D:{e.Category}] {e.Output?.TrimEnd()}");
                if (e.Severity == OutputEvent.SeverityValue.Error && !string.IsNullOrWhiteSpace(e.Output))
                    _lastErrorOutput = e.Output.TrimEnd();
            });

            // X16D re-resolves and re-verifies breakpoints asynchronously once a file's
            // debugger info actually loads (see "Loading debugger info for '...'" in its own
            // log) - a breakpoint set before that point legitimately comes back unverified from
            // set_breakpoints, then flips to verified via this event once the code is in memory.
            // Track the latest known state per breakpoint id so callers can see the settled
            // status instead of just the immediate snapshot.
            _host.RegisterEventType<BreakpointEvent>(OnBreakpointEvent);

            _pumpThread = new Thread(() =>
            {
                _host.Run();
                _host.WaitForReader();
            })
            {
                IsBackground = true,
                Name = "X16M DAP pump",
            };
            _pumpThread.Start();

            _active = true;
            Terminated = false;
            LastStop = null;
            _lastErrorOutput = null;
            ClearKnownBreakpoints();
            _projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "";
            var cwd = string.IsNullOrWhiteSpace(workspaceFolder)
                ? FindWorkspaceFolder(projectPath)
                : Path.GetFullPath(workspaceFolder);
            Log($"Workspace folder (launch cwd): {cwd}");

            _host.SendRequestSync(new InitializeRequest("x16m"));

            var launchRequest = new LaunchRequest
            {
                NoDebug = false,
                ConfigurationProperties = new Dictionary<string, JToken?>
                {
                    ["program"] = projectPath,
                    // What VS Code would send as ${workspaceRoot}: a project's basePath defaults
                    // to it, and a relative basePath resolves against it.
                    ["cwd"] = cwd,
                    ["stopOnEntry"] = stopOnEntry,
                    // X16D otherwise takes stepping from the project file's startStepping, the caller's choice must win.
                    ["stopOnEntryOverride"] = stopOnEntry,
                    // Overrides the project file's muteAudio. Silences output only, emulation is unaffected.
                    ["muteAudioOverride"] = muteAudio,
                },
            };

            // launch's own handling compiles the project synchronously, so its response can be
            // seconds away - a captured VS Code session confirms X16D still processes other
            // requests concurrently while that's in flight, and VS Code exploits exactly that by
            // pipelining setBreakpoints immediately behind launch rather than waiting for its
            // response first. SendRequestSync would block until launch's own response arrives,
            // defeating that; the async SendRequest just queues the write and returns, letting
            // setBreakpoints (below) go out right behind it on the wire, same as VS Code.
            _host.SendRequest<LaunchArguments>(
                launchRequest,
                completionFunc: _ => launchCompletion.TrySetResult(),
                errorFunc: (_, ex) => launchCompletion.TrySetException(ex));

            if (initialBreakpoints is not null)
            {
                foreach (var spec in initialBreakpoints)
                    initialBreakpointResults.AddRange(SendSetBreakpoints(spec.File, spec.Lines));
            }
        }

        try
        {
            await launchCompletion.Task;
        }
        catch (Exception ex)
        {
            // X16D exiting mid-launch faults this with an OperationCanceledException, which the
            // MCP SDK reads as "caller cancelled" and answers with silence instead of an error.
            // Rethrow as a plain exception so it's actually reported.
            var detail = _lastErrorOutput ?? ex.Message;
            throw new McpException($"X16D exited before completing launch: {detail}");
        }

        // VS Code never sends configurationDone (X16D's initialize response doesn't advertise
        // support for it, and HandleConfigurationDoneRequest is a pure no-op) - but VS Code also
        // doesn't need any further signal, since its breakpoints are already in place by the
        // time launch finishes (see above). configurationDone is still sent here as a way to
        // wait briefly for a stop in case the target happens to produce an early one anyway (e.g.
        // an early StartStepping-style pause). Not every target does, though, and this wait must
        // never block launch_project for long when one doesn't come - so a timeout here means
        // "still running", not an error to propagate.
        StopOutcome stop;
        try
        {
            stop = await SendAndWaitForStop(() => _host!.SendRequestSync(new ConfigurationDoneRequest()), LaunchInitialStopTimeout);
        }
        catch (McpException)
        {
            stop = new StopOutcome(Terminated: false, Stopped: null);
        }

        return new LaunchOutcome(stop, initialBreakpointResults, romWarning);
    }

    /// <summary>
    /// The folder VS Code would have open for this project, so a project file behaves the same
    /// through X16M as it does there: the nearest folder at or above the project file holding a
    /// .vscode folder, or the project file's own folder if there isn't one. Stops short of the
    /// user's home folder, as VS Code keeps its own settings in ~/.vscode.
    /// </summary>
    public static string FindWorkspaceFolder(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        for (var dir = new DirectoryInfo(projectDirectory); dir != null; dir = dir.Parent)
        {
            if (!string.IsNullOrEmpty(home) && string.Equals(Path.TrimEndingDirectorySeparator(dir.FullName), Path.TrimEndingDirectorySeparator(home), comparison))
                break;

            if (Directory.Exists(Path.Combine(dir.FullName, ".vscode")))
                return dir.FullName;
        }

        return projectDirectory;
    }

    /// <summary>
    /// Runs `X16D --buildOnly` as a one-shot child process to compile a project (.json) or a
    /// single .bmasm/.asm source file, then exits - no ROM load, no DAP handshake, nothing run.
    /// outputFolder is where the compiled program (.prg etc) is written; binFolder is just the
    /// template engine's intermediate artifacts, not the compiled program itself. Spawns its own
    /// separate X16D process and touches none of this session's own state (_process/_host/etc.),
    /// so it's safe to call regardless of whether a session from Launch/Attach is currently active.
    /// </summary>
    public async Task<(int ExitCode, string Output)> Build(string target, string? buildFolder = null, string? outputFolder = null, string? binFolder = null)
    {
        if (_connection is not X16DConnection.Spawn spawn)
            throw new McpException(_connection is X16DConnection.Tcp
                ? "build_project needs a local X16D to spawn, so it can't run while X16M is set to connect to an already-running X16D (--x16d-host/--x16d-port)."
                : "build_project needs a local X16D to spawn, but the one bundled with X16M wasn't found and no --x16d/X16D_PATH was given. Reinstall X16M, or restart it with --x16d pointing at an X16D executable.");

        if (!File.Exists(spawn.ExecutablePath))
            throw new McpException($"X16D executable not found at '{spawn.ExecutablePath}'.");

        if (!File.Exists(target))
            throw new McpException($"Build target not found at '{target}'.");

        // A project file defaults to the same workspace folder launch_project uses, so its paths
        // resolve the same way in both. A bare source file has no such paths, so its own folder.
        var folder = buildFolder
            ?? (string.Equals(Path.GetExtension(target), ".json", StringComparison.OrdinalIgnoreCase)
                ? FindWorkspaceFolder(target)
                : Path.GetDirectoryName(Path.GetFullPath(target)))
            ?? Environment.CurrentDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = spawn.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(spawn.ExecutablePath)) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--buildOnly");
        psi.ArgumentList.Add("--buildTarget");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("--buildFolder");
        psi.ArgumentList.Add(folder);
        if (!string.IsNullOrWhiteSpace(outputFolder))
        {
            psi.ArgumentList.Add("--outputFolder");
            psi.ArgumentList.Add(outputFolder);
        }
        if (!string.IsNullOrWhiteSpace(binFolder))
        {
            psi.ArgumentList.Add("--binFolder");
            psi.ArgumentList.Add(binFolder);
        }

        using var process = Process.Start(psi) ?? throw new McpException("Failed to start X16D.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        var output = stderr.Length == 0 ? stdout : $"{stdout}{Environment.NewLine}{stderr}".Trim();

        return (process.ExitCode, output);
    }

    private static string? CheckRomWarning(string x16dExecutablePath)
    {
        var x16dDirectory = Path.GetDirectoryName(Path.GetFullPath(x16dExecutablePath)) ?? "";
        if (RomLocator.X16DFindsRomUnaided(x16dDirectory) || File.Exists(RomLocator.UserRomPath))
            return null;

        return "No X16 ROM found: there's no rom.bin next to X16D or in the user's BitMagic folder " +
               $"('{RomLocator.UserRomFolder}'), and the BITMAGIC_ROM environment variable isn't set " +
               "to one either. Launch will likely fail unless this project points at a ROM some other " +
               "way (its romFile). Call fetch_rom to download the latest ROM into the BitMagic folder, " +
               "then launch again.";
    }

    private (Stream input, Stream output) StartProcess(X16DConnection.Spawn spawn, string? workingDirectory)
    {
        if (!File.Exists(spawn.ExecutablePath))
            throw new McpException($"X16D executable not found at '{spawn.ExecutablePath}'.");

        var psi = new ProcessStartInfo
        {
            FileName = spawn.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(spawn.ExecutablePath)) ?? Environment.CurrentDirectory,
        };

        // X16D itself only looks in its working directory and BITMAGIC_ROM, not the user's
        // BitMagic folder that fetch_rom (and X16E --fetch-rom) save to, so point it there.
        var romForX16D = RomLocator.RomToPassToX16D(psi.WorkingDirectory);
        if (romForX16D is not null)
        {
            psi.Environment[RomLocator.RomEnvironmentVariable] = romForX16D;
            Log($"Passing ROM to X16D via {RomLocator.RomEnvironmentVariable}: {romForX16D}");
        }

        var process = Process.Start(psi) ?? throw new McpException("Failed to start X16D.");
        _process = process;

        // If X16D dies (crash, killed externally, anything short of a clean DAP terminate/exit)
        // nothing would otherwise resolve a pending continue/step/launch wait, leaving the
        // caller hanging for the full timeout instead of failing immediately. Captures the local
        // rather than the _process field, which Shutdown() may already have nulled out by the
        // time this fires.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var exitCode = -1;
            try { exitCode = process.ExitCode; } catch { /* ignore */ }
            Log($"[X16D] process exited unexpectedly (code {exitCode})");
            OnTerminated(new TerminatedEvent());
        };

        // Drain stderr so X16D's own diagnostics can't fill the pipe and stall it; never let
        // anything from the child reach our stdout, since that's the MCP JSON-RPC channel.
        _ = Task.Run(() => DrainStreamAsync(_process.StandardError));

        return (_process.StandardInput.BaseStream, _process.StandardOutput.BaseStream);
    }

    private (Stream input, Stream output) ConnectTcp(X16DConnection.Tcp tcp)
    {
        _tcpClient = new TcpClient();
        _tcpClient.Connect(tcp.Host, tcp.Port);

        // DAP over TCP is one duplex stream per connection, same as X16D's own server mode uses
        // (X16D/Program.cs passes the same NetworkStream as both stdin and stdout).
        var stream = _tcpClient.GetStream();
        return (stream, stream);
    }

    public async Task<IReadOnlyList<Breakpoint>> SetBreakpoints(string file, IReadOnlyList<int> lines)
    {
        await EnsureSessionAsync();
        RequireNotAttached("set_breakpoints");
        return SendSetBreakpoints(file, lines);
    }

    // Same category as SetBreakpoints - configuring what causes a stop - so restricted the same
    // way: only the owning session should change it, not an attach_to_session connection watching
    // one VSCode already owns.
    public async Task<IReadOnlyList<Breakpoint>> SetExceptionBreakpoints(IReadOnlyList<string> filters)
    {
        await EnsureSessionAsync();
        RequireNotAttached("set_exception_breakpoints");

        var request = new SetExceptionBreakpointsRequest(filters.ToList());
        var response = _host!.SendRequestSync(request);
        return response.Breakpoints;
    }

    // A read, like GetStackTrace/Evaluate - available in attach mode too.
    public async Task<ExceptionInfoResponse> GetExceptionInfo(int threadId = 1)
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new ExceptionInfoRequest(threadId));
    }

    // Independent of SetBreakpoints' source-line breakpoints - same restriction, same reason.
    public async Task<IReadOnlyList<Breakpoint>> SetInstructionBreakpoints(IReadOnlyList<string> addresses)
    {
        await EnsureSessionAsync();
        RequireNotAttached("set_instruction_breakpoints");

        var request = new SetInstructionBreakpointsRequest(addresses.Select(a => new InstructionBreakpoint(a)).ToList());
        var response = _host!.SendRequestSync(request);
        ReplaceBreakpointGroup(InstructionGroup, response.Breakpoints);
        return response.Breakpoints;
    }

    // DAP's setFunctionBreakpoints, repurposed server-side (BreakpointManager.HandleFunctionBreakpointsRequest)
    // for X16 hardware breakpoints (vram(...)/vsync(...)/beam(...)/line(...) expressions) rather than named functions.
    public async Task<IReadOnlyList<Breakpoint>> SetHardwareBreakpoints(IReadOnlyList<string> expressions)
    {
        await EnsureSessionAsync();
        RequireNotAttached("set_hardware_breakpoints");

        var request = new SetFunctionBreakpointsRequest(expressions.Select(e => new FunctionBreakpoint(e)).ToList());
        var response = _host!.SendRequestSync(request);
        ReplaceBreakpointGroup(HardwareGroup, response.Breakpoints);
        return response.Breakpoints;
    }

    public async Task<PaletteRequestResponse> GetPalette()
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new PaletteRequest());
    }

    // First call (locations: null) scans the whole "main" space fresh. Pass the previous
    // response's Locations straight back to narrow down with a different searchType - the classic
    // "Cheat Engine" style value-scanning workflow. Location is an opaque id (it encodes which RAM
    // bank a match came from for banked addresses) - round-trip it, don't try to use it as a plain
    // address elsewhere.
    public async Task<MemoryValueTrackerResponse> FindMemoryValue(uint toFind, string searchType, string searchWidth, IReadOnlyList<MemoryValueDto>? locations)
    {
        await EnsureSessionAsync();

        var request = new MemoryValueTrackerRequest();
        request.Args.ToFind = toFind;
        request.Args.SearchType = searchType;
        request.Args.SearchWidth = searchWidth;
        request.Args.Locations = locations?.ToArray();

        return _host!.SendRequestSync(request);
    }

    // Shared by SetBreakpoints and Launch(initialBreakpoints:) - the latter calls this directly
    // (session is already active by then) immediately after sending launch, before
    // configurationDone, so the breakpoint is queued as close to VS Code's own pipelined
    // launch->setBreakpoints sequence as possible.
    private IReadOnlyList<Breakpoint> SendSetBreakpoints(string file, IReadOnlyList<int> lines)
    {
        // A relative path means nothing resolved against this process's own working directory,
        // which the caller has no visibility into or control over - resolve it against the
        // launched project's directory instead, which is what a caller actually means by
        // "this source file".
        var basePath = Path.IsPathRooted(file) || string.IsNullOrEmpty(_projectDirectory)
            ? file
            : Path.Combine(_projectDirectory, file);
        var fullPath = Path.GetFullPath(basePath);
        var source = new Source { Name = Path.GetFileName(fullPath), Path = fullPath };
        var breakpoints = lines.Select(l => new SourceBreakpoint(l)).ToList();

        var request = new SetBreakpointsRequest(source) { Breakpoints = breakpoints };
        var response = _host!.SendRequestSync(request);

        ReplaceBreakpointGroup(FileGroupPrefix + fullPath, response.Breakpoints);

        return response.Breakpoints;
    }

    private void ReplaceBreakpointGroup(string group, IEnumerable<Breakpoint> breakpoints)
    {
        lock (_breakpointGroups)
            _breakpointGroups[group] = breakpoints.ToList();
    }

    private void ClearKnownBreakpoints()
    {
        lock (_breakpointGroups)
            _breakpointGroups.Clear();
    }

    /// <summary>
    /// Current known state of every breakpoint set so far, kept up to date by BreakpointEvents -
    /// use this to see whether a breakpoint that came back unverified from set_breakpoints has
    /// since become verified (X16D verifies a breakpoint once the file it belongs to actually
    /// loads, which can happen well after set_breakpoints returns).
    /// </summary>
    public IReadOnlyList<KnownBreakpoint> GetKnownBreakpoints()
    {
        lock (_breakpointGroups)
        {
            return _breakpointGroups
                .OrderBy(g => g.Key == InstructionGroup ? 1 : g.Key == HardwareGroup ? 2 : 0)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.Value.Select(b => g.Key switch
                {
                    InstructionGroup => new KnownBreakpoint(BreakpointKind.Instruction, null, b),
                    HardwareGroup => new KnownBreakpoint(BreakpointKind.Hardware, null, b),
                    _ => new KnownBreakpoint(BreakpointKind.Source, g.Key[FileGroupPrefix.Length..], b),
                }))
                .ToList();
        }
    }

    public Task<StopOutcome> ContinueAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new ContinueRequest(threadId)), timeout);

    public Task<StopOutcome> StepOverAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new NextRequest(threadId)), timeout);

    public Task<StopOutcome> StepIntoAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new StepInRequest(threadId)), timeout);

    public Task<StopOutcome> StepOutAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new StepOutRequest(threadId)), timeout);

    // Unlike Continue/Step, Pause doesn't wait for a request-side action to trigger the stop -
    // the target's already running freely, so the very next StoppedEvent to arrive after this is
    // sent (there's nothing else that would produce one) is the pause taking effect.
    public Task<StopOutcome> PauseAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new PauseRequest(threadId)), timeout);

    public async Task<StackTraceResponse> GetStackTrace(int threadId = 1, int startFrame = 0, int levels = 20)
    {
        await EnsureSessionAsync();
        var request = new StackTraceRequest(threadId) { StartFrame = startFrame, Levels = levels };
        return _host!.SendRequestSync(request);
    }

    // frameId only matters for the "Locals" scope (X16Debug rebuilds it against whatever
    // frame you pass); every other scope (Globals, CPU, VERA, ...) ignores it and comes
    // back the same regardless - 0 is a harmless default when you don't care.
    public async Task<ScopesResponse> GetScopes(int frameId = 0)
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new ScopesRequest(frameId));
    }

    public async Task<VariablesResponse> GetVariables(int variablesReference)
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new VariablesRequest(variablesReference));
    }

    public async Task<EvaluateResponse> Evaluate(string expression, int? frameId = null)
    {
        await EnsureSessionAsync();
        var request = new EvaluateRequest(expression)
        {
            FrameId = frameId,
            Context = EvaluateArguments.ContextValue.Repl,
        };
        return _host!.SendRequestSync(request);
    }

    public async Task<DisassembleResponse> Disassemble(string memoryReference, int instructionCount)
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new DisassembleRequest(memoryReference, instructionCount));
    }

    public async Task<ReadMemoryResponse> ReadMemory(string memoryReference, int count, int offset = 0)
    {
        await EnsureSessionAsync();
        var request = new ReadMemoryRequest(memoryReference, count) { Offset = offset };
        return _host!.SendRequestSync(request);
    }

    // Available in both spawn/launch and attach mode - amending memory isn't a control-flow
    // operation, so it's not restricted the way SetBreakpoints/Continue/Step are.
    public async Task<WriteMemoryResponse> WriteMemory(string memoryReference, byte[] data, int offset = 0)
    {
        await EnsureSessionAsync();
        var request = new WriteMemoryRequest(memoryReference, Convert.ToBase64String(data)) { Offset = offset };
        return _host!.SendRequestSync(request);
    }

    public async Task<MemorySearchResponse> SearchMemory(string memoryReference, string patternBase64, bool caseInsensitive, int maxResults = 500)
    {
        await EnsureSessionAsync();

        var request = new MemorySearchRequest();
        request.Args.MemoryReference = memoryReference;
        request.Args.Pattern = patternBase64;
        request.Args.CaseInsensitive = caseInsensitive;
        request.Args.MaxResults = maxResults;

        return _host!.SendRequestSync(request);
    }

    // Amending input isn't a control-flow operation (unlike Continue/Step), so - like
    // WriteMemory - it's available in both spawn/launch and attach mode.
    public async Task<KeyboardInputRequestResponse> SendKey(string key, bool down)
    {
        await EnsureSessionAsync();

        var request = new KeyboardInputRequest();
        request.Args.Key = key;
        request.Args.Down = down;

        return _host!.SendRequestSync(request);
    }

    public async Task<MouseInputRequestResponse> SendMouse(int deltaX, int deltaY, bool left, bool right, bool middle)
    {
        await EnsureSessionAsync();

        var request = new MouseInputRequest();
        request.Args.DeltaX = deltaX;
        request.Args.DeltaY = deltaY;
        request.Args.Left = left;
        request.Args.Right = right;
        request.Args.Middle = middle;

        return _host!.SendRequestSync(request);
    }

    // Recording observes rather than controls execution, so like input it's available in both
    // spawn/launch and attach mode.
    public async Task<StartAudioRecordingRequestResponse> StartAudioRecording(string path)
    {
        await EnsureSessionAsync();

        var request = new StartAudioRecordingRequest();
        request.Args.Path = path;

        return _host!.SendRequestSync(request);
    }

    public async Task<StopAudioRecordingRequestResponse> StopAudioRecording()
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new StopAudioRecordingRequest());
    }

    public async Task<LayerRequestResponse> GetLayers()
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new LayerRequest());
    }

    public async Task<ScreenRequestResponse> GetScreen(string? renderMode = null, int? renderToX = null, int? renderToY = null)
    {
        await EnsureSessionAsync();
        var request = new ScreenRequest();
        request.Args.RenderMode = renderMode;
        request.Args.RenderToX = renderToX;
        request.Args.RenderToY = renderToY;
        return _host!.SendRequestSync(request);
    }

    public async Task<SpriteRequestResponse> GetSprites()
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new SpriteRequest());
    }

    /// <summary>Most recent page of executed instructions (up to 1024, most-recent-first).</summary>
    public async Task<HistoryRequestResponse> GetHistory()
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new HistoryRequest());
    }

    // Taking a snapshot doesn't change the program, so like reading memory it's available in both
    // spawn/launch and attach mode. X16D takes it at the next instruction if the target's running.
    public async Task<SnapshotRequestResponse> TakeSnapshot()
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new TakeSnapshotRequest());
    }

    public async Task<GetSnapshotsRequestResponse> GetSnapshots()
    {
        await EnsureSessionAsync();
        return _host!.SendRequestSync(new GetSnapshotsRequest());
    }

    public async Task<SnapshotRequestResponse> DeleteSnapshot(int id)
    {
        await EnsureSessionAsync();

        var request = new DeleteSnapshotRequest();
        request.Args.Id = id;

        return _host!.SendRequestSync(request);
    }

    public async Task<SnapshotRequestResponse> RenameSnapshot(int id, string name)
    {
        await EnsureSessionAsync();

        var request = new RenameSnapshotRequest();
        request.Args.Id = id;
        request.Args.Name = name;

        return _host!.SendRequestSync(request);
    }

    // Restoring moves the program and sends a stop, so it's control flow like Continue/Step - not
    // available while attached. X16D needs the target paused, so if it's running it's paused first.
    // Waits for the stop X16D sends on success.
    public async Task<(SnapshotRequestResponse Response, StopOutcome? Stop)> RestoreSnapshot(int id, TimeSpan? timeout = null)
    {
        await EnsureSessionAsync();
        RequireNotAttached("restore_snapshot");

        await _stepLock.WaitAsync();
        try
        {
            if (Terminated)
                throw new McpException("The target has already terminated. Call launch_project to start a new session.");

            var (response, stop) = await SendRestore(id, timeout);

            if (!response.Success && response.Running)
            {
                var pending = new TaskCompletionSource<StopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingStop = pending;
                _host!.SendRequestSync(new PauseRequest(1));

                var paused = await WaitWithTimeout(pending.Task, timeout ?? DefaultTimeout);
                if (paused.Terminated)
                    return (response, paused);

                (response, stop) = await SendRestore(id, timeout);
            }

            return (response, stop);
        }
        finally
        {
            _stepLock.Release();
        }
    }

    // only call while holding _stepLock
    private async Task<(SnapshotRequestResponse Response, StopOutcome? Stop)> SendRestore(int id, TimeSpan? timeout)
    {
        var tcs = new TaskCompletionSource<StopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStop = tcs;

        var request = new RestoreSnapshotRequest();
        request.Args.Id = id;

        var response = _host!.SendRequestSync(request);

        if (!response.Success)
            return (response, null);

        return (response, await WaitWithTimeout(tcs.Task, timeout ?? DefaultTimeout));
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            // Attach mode doesn't own the session - X16D rejects a terminating disconnect from
            // it anyway (see X16Debug.RequireNotAttachOnly) - so just drop our own connection.
            if (_host is not null && IsActive && !IsAttached)
            {
                try { _host.SendRequestSync(new DisconnectRequest { TerminateDebuggee = true }); }
                catch { /* best-effort; we're tearing the process down regardless */ }
            }

            Shutdown();
        }
    }

    private void RequireNotAttached(string action)
    {
        if (IsAttached)
            throw new McpException($"{action} is not available while attached to an existing session - use VSCode to control it.");
    }

    private async Task<StopOutcome> SendAndWaitForStop(Action sendRequest, TimeSpan? timeout)
    {
        await EnsureSessionAsync();
        RequireNotAttached("continue/step");

        // Only one continue/step can be in flight at a time - see _stepLock's declaration for
        // why. A second call simply waits its turn rather than racing on _pendingStop.
        await _stepLock.WaitAsync();
        try
        {
            if (Terminated)
                throw new McpException("The target has already terminated. Call launch_project to start a new session.");

            var tcs = new TaskCompletionSource<StopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingStop = tcs;

            sendRequest();

            return await WaitWithTimeout(tcs.Task, timeout ?? DefaultTimeout);
        }
        finally
        {
            _stepLock.Release();
        }
    }

    private static async Task<StopOutcome> WaitWithTimeout(Task<StopOutcome> waitTask, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout));
        if (completed != waitTask)
            throw new McpException($"Timed out after {timeout.TotalSeconds:0}s waiting for the target to stop.");

        return await waitTask;
    }

    private void OnStopped(StoppedEvent e)
    {
        LastStop = e;
        _pendingStop?.TrySetResult(new StopOutcome(false, e));
    }

    private void OnTerminated(TerminatedEvent e)
    {
        Terminated = true;
        _pendingStop?.TrySetResult(new StopOutcome(true, null));
    }

    private void OnBreakpointEvent(BreakpointEvent e)
    {
        var breakpoint = e.Breakpoint;

        // Matched by id, as DAP specifies. One without an id, or that X16M didn't set (eg in a
        // session it's attached to), isn't tracked.
        if (breakpoint.Id.HasValue)
        {
            lock (_breakpointGroups)
            {
                foreach (var group in _breakpointGroups.Values)
                {
                    var index = group.FindIndex(b => b.Id == breakpoint.Id);
                    if (index == -1)
                        continue;

                    if (e.Reason == BreakpointEvent.ReasonValue.Removed)
                        group.RemoveAt(index);
                    else
                        group[index] = breakpoint;

                    break;
                }
            }
        }

        Log($"[X16D:breakpoint] {e.Reason} - line {breakpoint.Line}: {(breakpoint.Verified ? "verified" : "NOT verified")}" +
            $"{(string.IsNullOrEmpty(breakpoint.Message) ? "" : $" ({breakpoint.Message})")}");
    }

    private async Task DrainStreamAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            Log($"[X16D] {line}");
    }

    // Console.Error is still written to as well - handy when running X16M interactively (e.g.
    // spawned directly under a raw JSON-RPC harness) rather than as a backgrounded MCP process.
    private void Log(string message)
    {
        Console.Error.WriteLine(message);
        try { _logWriter?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"); } catch { /* ignore */ }
    }

    private void Shutdown()
    {
        try { _host?.Stop(); } catch { /* ignore */ }

        // Only kill X16D when we spawned it ourselves. In TCP mode it's someone else's process
        // (e.g. running under the Visual Studio debugger) - just close our connection to it.
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        try { _tcpClient?.Dispose(); } catch { /* ignore */ }

        _process?.Dispose();
        _process = null;
        _tcpClient = null;
        _host = null;
        _active = false;
        IsAttached = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Shutdown();
        }

        _stepLock.Dispose();
        _logWriter?.Dispose();
    }
}
