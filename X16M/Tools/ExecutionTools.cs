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

    internal static string Describe(StopOutcome outcome)
    {
        if (outcome.Terminated || outcome.Stopped is null)
            return "Target terminated.";

        var e = outcome.Stopped;
        return $"Stopped (reason: {e.Reason}{(string.IsNullOrEmpty(e.Text) ? "" : $", {e.Text}")}, threadId: {e.ThreadId}).";
    }
}
