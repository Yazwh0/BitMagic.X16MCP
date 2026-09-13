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
        [Description("Path to the source file the breakpoints belong to.")] string file,
        [Description("1-based source line numbers to set breakpoints on.")] int[] lines)
    {
        var breakpoints = session.SetBreakpoints(file, lines);

        var summary = breakpoints.Select(b => b.Verified
            ? $"line {b.Line}: verified"
            : $"line {b.Line}: NOT verified{(string.IsNullOrEmpty(b.Message) ? "" : $" ({b.Message})")}");

        return string.Join(Environment.NewLine, summary);
    }
}
