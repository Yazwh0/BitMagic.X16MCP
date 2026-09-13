namespace X16M.Dap;

/// <summary>How <see cref="DapSession"/> reaches X16D.</summary>
public abstract record X16DConnection
{
    /// <summary>Spawns X16D as a child process and talks DAP over its stdin/stdout, exactly as VS Code does. X16M owns the process's lifetime.</summary>
    public sealed record Spawn(string ExecutablePath) : X16DConnection;

    /// <summary>
    /// Connects to an X16D already running with `--dapport`, e.g. one you started yourself under
    /// the Visual Studio debugger. X16M does not own its lifetime - disconnecting closes the
    /// socket but leaves X16D running for the next connection.
    /// </summary>
    public sealed record Tcp(string Host, int Port) : X16DConnection;
}
