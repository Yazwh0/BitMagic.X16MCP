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
    [Description("Launches an X16 debug session against a BitMagic project (a .json project file, or a .bmasm source file directly). Stops the target right at its entry point by default (stopOnEntry), so this call itself reports \"Stopped\" rather than \"Still running\" - the recommended flow from here is set_breakpoints, then continue_execution. If you pass stopOnEntry: false instead, strongly prefer passing breakpoints here too rather than a follow-up set_breakpoints call: some targets run to completion in well under a second once launched, faster than a separate tool call can land, so breakpoints given here are queued immediately behind the launch request itself (the same way VS Code's own DAP client does it) instead of racing the target's own execution speed.")]
    public static async Task<string> LaunchProject(
        DapSession session,
        [Description("Path to the project's .json file (or a .bmasm file) to launch.")] string projectPath,
        [Description("Breakpoints to set before the target starts running. With the default stopOnEntry: true these aren't needed to avoid a race (nothing runs until continue_execution), but still useful to have verified and ready before the first continue.")] BreakpointSpec[]? breakpoints = null,
        [Description("Stop the target at its very first instruction, before anything runs. On by default so you get a safe moment to inspect state and set breakpoints; pass false to let it start running immediately instead.")] bool stopOnEntry = true)
    {
        var outcome = await session.Launch(projectPath, breakpoints, stopOnEntry: stopOnEntry);

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
