using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class SessionTools
{
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
    [Description("Ends the current debug session and terminates the X16D child process.")]
    public static string Disconnect(DapSession session)
    {
        session.Disconnect();
        return "Disconnected.";
    }
}
