using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
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

    private readonly X16DConnection _connection;
    private readonly object _gate = new();

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

    // Latest known verification state per breakpoint id, kept current by OnBreakpointEvent so a
    // caller can see whether a breakpoint has since become verified without re-sending
    // set_breakpoints.
    private readonly Dictionary<int, Breakpoint> _breakpointsById = new();

    // A live MCP session's stderr isn't something a user can easily get to, so DAP traffic is
    // also written to a log file next to the executable (not the process's working directory,
    // which callers don't control) - overwritten fresh on each Launch.
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "x16m-dap.log");
    private StreamWriter? _logWriter;

    public DapSession(X16DConnection connection)
    {
        _connection = connection;
    }

    public bool IsActive => _active;
    public StoppedEvent? LastStop { get; private set; }
    public bool Terminated { get; private set; }

    public async Task<LaunchOutcome> Launch(string projectPath, IReadOnlyList<BreakpointSpec>? initialBreakpoints = null, string? workingDirectory = null)
    {
        var launchCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialBreakpointResults = new List<Breakpoint>();
        string? romWarning;

        lock (_gate)
        {
            if (IsActive)
                throw new InvalidOperationException("A debug session is already running. Call disconnect first.");

            if (!File.Exists(projectPath))
                throw new FileNotFoundException($"Project file not found at '{projectPath}'.");

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
                _ => throw new NotSupportedException($"Unknown X16D connection type '{_connection.GetType()}'."),
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
            _projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "";

            _host.SendRequestSync(new InitializeRequest("x16m"));

            var launchRequest = new LaunchRequest
            {
                NoDebug = false,
                ConfigurationProperties = new Dictionary<string, JToken?>
                {
                    ["program"] = projectPath,
                    ["cwd"] = _projectDirectory,
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
            throw new InvalidOperationException($"X16D exited before completing launch: {detail}");
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
        catch (TimeoutException)
        {
            stop = new StopOutcome(Terminated: false, Stopped: null);
        }

        return new LaunchOutcome(stop, initialBreakpointResults, romWarning);
    }

    private static string? CheckRomWarning(string x16dExecutablePath)
    {
        var x16dDirectory = Path.GetDirectoryName(Path.GetFullPath(x16dExecutablePath)) ?? "";
        if (File.Exists(Path.Combine(x16dDirectory, "rom.bin")))
            return null;

        var env = Environment.GetEnvironmentVariable("BITMAGIC_ROM");
        if (!string.IsNullOrWhiteSpace(env) && (File.Exists(env) || File.Exists(Path.Combine(env, "rom.bin"))))
            return null;

        return "No X16 ROM found next to X16D, and the BITMAGIC_ROM environment variable isn't " +
               "set to one either - launch will likely fail unless this project points at a ROM " +
               "some other way. Download a ROM from the official Commander X16 releases and " +
               "either place it as 'rom.bin' next to X16D, or set BITMAGIC_ROM to its file path " +
               "(or the folder containing it).";
    }

    private (Stream input, Stream output) StartProcess(X16DConnection.Spawn spawn, string? workingDirectory)
    {
        if (!File.Exists(spawn.ExecutablePath))
            throw new FileNotFoundException($"X16D executable not found at '{spawn.ExecutablePath}'.");

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

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start X16D.");
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

    public IReadOnlyList<Breakpoint> SetBreakpoints(string file, IReadOnlyList<int> lines)
    {
        RequireActive();
        return SendSetBreakpoints(file, lines);
    }

    // Shared by SetBreakpoints and Launch(initialBreakpoints:) - the latter calls this directly
    // (bypassing RequireActive, which would already be satisfied) immediately after sending
    // launch, before configurationDone, so the breakpoint is queued as close to VS Code's own
    // pipelined launch->setBreakpoints sequence as possible.
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

        foreach (var breakpoint in response.Breakpoints)
        {
            if (breakpoint.Id.HasValue)
                _breakpointsById[breakpoint.Id.Value] = breakpoint;
        }

        return response.Breakpoints;
    }

    /// <summary>
    /// Current known state of every breakpoint set so far, kept up to date by BreakpointEvents -
    /// use this to see whether a breakpoint that came back unverified from set_breakpoints has
    /// since become verified (X16D verifies a breakpoint once the file it belongs to actually
    /// loads, which can happen well after set_breakpoints returns).
    /// </summary>
    public IReadOnlyList<Breakpoint> GetKnownBreakpoints() => _breakpointsById.Values.ToList();

    public Task<StopOutcome> ContinueAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new ContinueRequest(threadId)), timeout);

    public Task<StopOutcome> StepOverAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new NextRequest(threadId)), timeout);

    public Task<StopOutcome> StepIntoAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new StepInRequest(threadId)), timeout);

    public Task<StopOutcome> StepOutAsync(int threadId = 1, TimeSpan? timeout = null)
        => SendAndWaitForStop(() => _host!.SendRequestSync(new StepOutRequest(threadId)), timeout);

    public StackTraceResponse GetStackTrace(int threadId = 1, int startFrame = 0, int levels = 20)
    {
        RequireActive();
        var request = new StackTraceRequest(threadId) { StartFrame = startFrame, Levels = levels };
        return _host!.SendRequestSync(request);
    }

    public EvaluateResponse Evaluate(string expression, int? frameId = null)
    {
        RequireActive();
        var request = new EvaluateRequest(expression)
        {
            FrameId = frameId,
            Context = EvaluateArguments.ContextValue.Repl,
        };
        return _host!.SendRequestSync(request);
    }

    public DisassembleResponse Disassemble(string memoryReference, int instructionCount)
    {
        RequireActive();
        return _host!.SendRequestSync(new DisassembleRequest(memoryReference, instructionCount));
    }

    public ReadMemoryResponse ReadMemory(string memoryReference, int count)
    {
        RequireActive();
        return _host!.SendRequestSync(new ReadMemoryRequest(memoryReference, count));
    }

    public MemorySearchResponse SearchMemory(string memoryReference, string patternBase64, bool caseInsensitive, int maxResults = 500)
    {
        RequireActive();

        var request = new MemorySearchRequest();
        request.Args.MemoryReference = memoryReference;
        request.Args.Pattern = patternBase64;
        request.Args.CaseInsensitive = caseInsensitive;
        request.Args.MaxResults = maxResults;

        return _host!.SendRequestSync(request);
    }

    public LayerRequestResponse GetLayers()
    {
        RequireActive();
        return _host!.SendRequestSync(new LayerRequest());
    }

    public SpriteRequestResponse GetSprites()
    {
        RequireActive();
        return _host!.SendRequestSync(new SpriteRequest());
    }

    /// <summary>Most recent page of executed instructions (up to 1024, most-recent-first).</summary>
    public HistoryRequestResponse GetHistory()
    {
        RequireActive();
        return _host!.SendRequestSync(new HistoryRequest());
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            if (_host is not null && IsActive)
            {
                try { _host.SendRequestSync(new DisconnectRequest { TerminateDebuggee = true }); }
                catch { /* best-effort; we're tearing the process down regardless */ }
            }

            Shutdown();
        }
    }

    private async Task<StopOutcome> SendAndWaitForStop(Action sendRequest, TimeSpan? timeout)
    {
        // Only one continue/step can be in flight at a time - see _stepLock's declaration for
        // why. A second call simply waits its turn rather than racing on _pendingStop.
        await _stepLock.WaitAsync();
        try
        {
            RequireActive();

            if (Terminated)
                throw new InvalidOperationException("The target has already terminated. Call launch_project to start a new session.");

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
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for the target to stop.");

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
        if (breakpoint.Id.HasValue)
            _breakpointsById[breakpoint.Id.Value] = breakpoint;

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

    private void RequireActive()
    {
        if (_host is null || !IsActive)
            throw new InvalidOperationException("No active debug session. Call launch_project first.");
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
