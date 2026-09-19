using System.ComponentModel;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class BreakpointTools
{
    [McpServerTool(Name = "set_breakpoints", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Sets the full set of source breakpoints for one file, replacing any previously set breakpoints in that file (matches DAP's setBreakpoints semantics).")]
    public static async Task<string> SetBreakpoints(
        DapSession session,
        [Description("Path to the source file the breakpoints belong to. A relative path resolves against the launched project's own directory.")] string file,
        [Description("1-based source line numbers to set breakpoints on.")] int[] lines)
    {
        var breakpoints = await session.SetBreakpoints(file, lines);

        if (breakpoints.Count == 0)
            return $"No breakpoints were set. Check '{file}' matches a source file in the launched project.";

        return Summarize(breakpoints, unverifiedSuffix: " - X16D verifies a breakpoint once its file actually loads, which may not have happened yet; use get_breakpoints to check again later");
    }

    [McpServerTool(Name = "get_breakpoints", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reports the current verification state of every breakpoint set so far. X16D verifies a breakpoint asynchronously once the file it belongs to actually loads, so a breakpoint that came back unverified from set_breakpoints may have since become verified.")]
    public static string GetBreakpoints(DapSession session)
    {
        var breakpoints = session.GetKnownBreakpoints();

        if (breakpoints.Count == 0)
            return "No breakpoints have been set.";

        return Summarize(breakpoints, unverifiedSuffix: "");
    }

    internal static string Summarize(IReadOnlyList<Breakpoint> breakpoints, string unverifiedSuffix)
    {
        var summary = breakpoints.Select(b => b.Verified
            ? $"line {b.Line}: verified"
            : $"line {b.Line}: NOT verified{(string.IsNullOrEmpty(b.Message) ? "" : $" ({b.Message})")}{unverifiedSuffix}");

        return string.Join(Environment.NewLine, summary);
    }
}
