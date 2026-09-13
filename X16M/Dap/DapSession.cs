using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using Thread = System.Threading.Thread;

namespace X16M.Dap;

/// <summary>Outcome of waiting for the target to stop after a continue/step request.</summary>
public sealed record StopOutcome(bool Terminated, StoppedEvent? Stopped);

/// <summary>
/// Speaks DAP to X16D via <see cref="DebugProtocolHost"/>, exactly as VS Code would - either by
/// spawning it directly, or by connecting to one already running with `--dapport` (so it can be
/// started separately, e.g. under the Visual Studio debugger). One session at a time; call
/// <see cref="Disconnect"/> before a new <see cref="Launch"/>.
/// </summary>
public sealed class DapSession : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

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

    public DapSession(X16DConnection connection)
    {
        _connection = connection;
    }

    public bool IsActive => _active;
    public StoppedEvent? LastStop { get; private set; }
    public bool Terminated { get; private set; }

    public void Launch(string projectPath, string? workingDirectory = null)
    {
        lock (_gate)
        {
            if (IsActive)
                throw new InvalidOperationException("A debug session is already running. Call disconnect first.");

            if (!File.Exists(projectPath))
                throw new FileNotFoundException($"Project file not found at '{projectPath}'.");

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
            _host.RegisterEventType<StoppedEvent>(OnStopped);
            _host.RegisterEventType<TerminatedEvent>(OnTerminated);
            _host.RegisterEventType<ExitedEvent>(_ => OnTerminated(new TerminatedEvent()));

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

            _host.SendRequestSync(new InitializeRequest("x16m"));

            var launchRequest = new LaunchRequest
            {
                NoDebug = false,
                ConfigurationProperties = new Dictionary<string, JToken?>
                {
                    ["program"] = projectPath,
                    ["cwd"] = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "",
                },
            };
            _host.SendRequestSync(launchRequest);
            _host.SendRequestSync(new ConfigurationDoneRequest());
        }
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

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start X16D.");

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

        var fullPath = Path.GetFullPath(file);
        var source = new Source { Name = Path.GetFileName(fullPath), Path = fullPath };
        var breakpoints = lines.Select(l => new SourceBreakpoint(l)).ToList();

        var request = new SetBreakpointsRequest(source) { Breakpoints = breakpoints };
        var response = _host!.SendRequestSync(request);
        return response.Breakpoints;
    }

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

    private static async Task DrainStreamAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            Console.Error.WriteLine($"[X16D] {line}");
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
    }
}
