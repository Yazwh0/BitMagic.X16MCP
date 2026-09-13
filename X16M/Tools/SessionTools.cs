using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "launch_project", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Launches an X16 debug session against a BitMagic project (a .json project file, or a .bmasm source file directly). Spawns X16D and runs the target to its initial stop/running state.")]
    public static string LaunchProject(
        DapSession session,
        [Description("Path to the project's .json file (or a .bmasm file) to launch.")] string projectPath)
    {
        session.Launch(projectPath);
        return session.Terminated
            ? "Target ran to completion immediately."
            : "Session launched.";
    }

    [McpServerTool(Name = "disconnect", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Ends the current debug session and terminates the X16D child process.")]
    public static string Disconnect(DapSession session)
    {
        session.Disconnect();
        return "Disconnected.";
    }
}
