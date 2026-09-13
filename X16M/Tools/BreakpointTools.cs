using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class BreakpointTools
{
    [McpServerTool(Name = "set_breakpoints", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Sets the full set of source breakpoints for one file, replacing any previously set breakpoints in that file (matches DAP's setBreakpoints semantics).")]
    public static string SetBreakpoints(
        DapSession session,
        [Description("Path to the source file the breakpoints belong to. A relative path resolves against the launched project's own directory.")] string file,
        [Description("1-based source line numbers to set breakpoints on.")] int[] lines)
    {
        var breakpoints = session.SetBreakpoints(file, lines);

        if (breakpoints.Count == 0)
            return $"No breakpoints were set. Check '{file}' matches a source file in the launched project.";

        var summary = breakpoints.Select(b => b.Verified
            ? $"line {b.Line}: verified"
            : $"line {b.Line}: NOT verified{(string.IsNullOrEmpty(b.Message) ? "" : $" ({b.Message})")}");

        return string.Join(Environment.NewLine, summary);
    }
}
