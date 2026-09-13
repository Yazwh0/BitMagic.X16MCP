using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "launch_project", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Launches an X16 debug session against a BitMagic project (a .json project file, or a .bmasm source file directly). Spawns X16D, compiles the project, and waits for its initial stop before returning, so breakpoints set immediately afterward are guaranteed to be in place before the target runs any further.")]
    public static async Task<string> LaunchProject(
        DapSession session,
        [Description("Path to the project's .json file (or a .bmasm file) to launch.")] string projectPath)
    {
        var outcome = await session.Launch(projectPath);
        return $"Launched. {ExecutionTools.Describe(outcome)}";
    }

    [McpServerTool(Name = "disconnect", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Ends the current debug session and terminates the X16D child process.")]
    public static string Disconnect(DapSession session)
    {
        session.Disconnect();
        return "Disconnected.";
    }
}
