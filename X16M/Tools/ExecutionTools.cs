using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class ExecutionTools
{
    [McpServerTool(Name = "continue_execution", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Resumes execution and waits for the target to hit a breakpoint, stop for another reason, or terminate.")]
    public static async Task<string> ContinueExecution(DapSession session, int threadId = 1)
        => Describe(await session.ContinueAsync(threadId));

    [McpServerTool(Name = "step_over", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Steps over the current source line (DAP 'next') and waits for the resulting stop.")]
    public static async Task<string> StepOver(DapSession session, int threadId = 1)
        => Describe(await session.StepOverAsync(threadId));

    [McpServerTool(Name = "step_into", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Steps into the next call (DAP 'stepIn') and waits for the resulting stop.")]
    public static async Task<string> StepInto(DapSession session, int threadId = 1)
        => Describe(await session.StepIntoAsync(threadId));

    [McpServerTool(Name = "step_out", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Steps out of the current routine (DAP 'stepOut') and waits for the resulting stop.")]
    public static async Task<string> StepOut(DapSession session, int threadId = 1)
        => Describe(await session.StepOutAsync(threadId));

    [McpServerTool(Name = "pause", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Pauses a running target immediately and waits for the resulting stop, rather than waiting for a breakpoint or exception to do it. Useful right after launch_project when the target is just running freely with nothing else set to stop it, or any time you want to look at current state without waiting for a specific condition.")]
    public static async Task<string> Pause(DapSession session, int threadId = 1)
        => Describe(await session.PauseAsync(threadId));

    internal static string Describe(StopOutcome outcome)
    {
        if (outcome.StillRunning)
            return "Still running - no stop observed yet. Set breakpoints now if you haven't, then call continue_execution (or step) to wait for the next one.";

        if (outcome.Terminated || outcome.Stopped is null)
            return "Target terminated.";

        var e = outcome.Stopped;
        return $"Stopped (reason: {e.Reason}{(string.IsNullOrEmpty(e.Text) ? "" : $", {e.Text}")}, threadId: {e.ThreadId}).";
    }
}
