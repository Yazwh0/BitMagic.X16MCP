using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "attach_to_session", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Explicitly attaches to an X16 debug session VSCode already launched and owns, to view/amend its state - not to control it. Every other tool already does this automatically on first use when idle, so this is only needed to check connectivity up front or to re-attach after a disconnect. Needs BitMagic.VSC's 'Run debug sessions through the same background process' setting on, and a debug session currently active there. Stepping, breakpoints, continue, and disconnect stay with VSCode (use its own debug tools/chat integration for those) - X16M's tools are for reading and amending X16-specific state (memory, sprites, palette, layers, CPU history) once attached.")]
    public static async Task<string> AttachToSession(
        DapSession session,
        [Description("Project directory to look for the running session's connection info in. Defaults to the current working directory.")] string? workspacePath = null)
    {
        var found = DapSession.FindConnectionInfo(workspacePath ?? Directory.GetCurrentDirectory());
        if (found is null)
            throw new InvalidOperationException("No running BitMagic debug session found. Make sure VSCode has 'Run debug sessions through the same background process' enabled and a debug session is active.");

        await session.Attach(found.Value.host, found.Value.port);
        return $"Attached to session on {found.Value.host}:{found.Value.port}.";
    }

    [McpServerTool(Name = "launch_project", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Launches an X16 debug session against a BitMagic project (a .json project file, or a .bmasm source file directly). Strongly prefer passing breakpoints here rather than calling set_breakpoints afterward: some targets run to completion in well under a second once launched, faster than a separate follow-up tool call can land, so breakpoints given here are queued immediately behind the launch request itself (the same way VS Code's own DAP client does it) instead of racing the target's own execution speed.")]
    public static async Task<string> LaunchProject(
        DapSession session,
        [Description("Path to the project's .json file (or a .bmasm file) to launch.")] string projectPath,
        [Description("Breakpoints to set before the target starts running. Strongly recommended over a follow-up set_breakpoints call - see this tool's own description for why.")] BreakpointSpec[]? breakpoints = null)
    {
        var outcome = await session.Launch(projectPath, breakpoints);

        var result = "";
        if (outcome.RomWarning is not null)
            result += outcome.RomWarning + Environment.NewLine;

        result += $"Launched. {ExecutionTools.Describe(outcome.Stop)}";
        if (outcome.InitialBreakpoints.Count > 0)
            result += Environment.NewLine + BreakpointTools.Summarize(outcome.InitialBreakpoints, unverifiedSuffix: " - X16D verifies a breakpoint once its file actually loads, which may not have happened yet; use get_breakpoints to check again later");

        return result;
    }

    [McpServerTool(Name = "disconnect", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Ends the current debug session and terminates the X16D child process. If attached via attach_to_session instead, this only drops X16M's own connection - the VSCode-owned session keeps running.")]
    public static string Disconnect(DapSession session)
    {
        session.Disconnect();
        return "Disconnected.";
    }
}
