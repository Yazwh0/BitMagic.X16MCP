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

    [McpServerTool(Name = "set_exception_breakpoints", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Sets which exception conditions stop the target, replacing whatever was set before (matches DAP's setExceptionBreakpoints semantics). Filters: \"BRK\" (a BRK instruction executes - off by default), \"EXP\" (an exception raised within code - on by default), \"FIO\" (a LOAD returned an error code - on by default). Pass only the ones you want enabled; leaving one out disables it, and an empty array disables all of them. Once the target stops for one of these, use get_exception_info to see which it was and why.")]
    public static async Task<string> SetExceptionBreakpoints(
        DapSession session,
        [Description("Filter ids to enable: any combination of \"BRK\", \"EXP\", \"FIO\". Empty array disables all of them.")] string[] filters)
    {
        var problems = await session.SetExceptionBreakpoints(filters);

        if (problems.Count > 0)
            return "Problem(s): " + string.Join("; ", problems.Select(b => b.Message ?? "unrecognised filter"));

        return filters.Length == 0 ? "All exception breakpoints disabled." : $"Enabled: {string.Join(", ", filters)}.";
    }

    [McpServerTool(Name = "get_exception_info", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reports why the target is currently stopped, when the stop was caused by an exception breakpoint (see set_exception_breakpoints) rather than a line breakpoint or a step. Call this after continue_execution/step_* reports a stop with reason \"exception\".")]
    public static async Task<string> GetExceptionInfo(DapSession session, int threadId = 1)
    {
        var info = await session.GetExceptionInfo(threadId);
        return $"{info.ExceptionId}: {info.Description}";
    }

    [McpServerTool(Name = "set_instruction_breakpoints", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Sets the full set of breakpoints by raw CPU address, replacing any previously set this way (matches DAP's setInstructionBreakpoints semantics) - independent of set_breakpoints' source-line breakpoints. Useful for code with no source mapping, or an address reached from more than one line.")]
    public static async Task<string> SetInstructionBreakpoints(
        DapSession session,
        [Description(InspectionTools.AddressDescription)] string[] addresses)
    {
        var parsed = new string[addresses.Length];
        for (var i = 0; i < addresses.Length; i++)
        {
            if (!InspectionTools.TryParseAddress(addresses[i], out var value))
                return $"Could not parse address '{addresses[i]}'. " + InspectionTools.AddressDescription;

            parsed[i] = value.ToString("X");
        }

        var breakpoints = await session.SetInstructionBreakpoints(parsed);

        if (breakpoints.Count == 0)
            return "No breakpoints were set.";

        var summary = breakpoints.Select(b => b.Verified
            ? $"0x{b.InstructionReference}: verified"
            : $"0x{b.InstructionReference}: NOT verified{(string.IsNullOrEmpty(b.Message) ? "" : $" ({b.Message})")}");

        return string.Join(Environment.NewLine, summary);
    }

    [McpServerTool(Name = "set_hardware_breakpoints", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Sets X16 hardware breakpoints, replacing any previously set this way. Wraps DAP's setFunctionBreakpoints, but X16D repurposes it for VRAM/VSync rather than named functions - there's no \"break when this proc is called\" here, source breakpoints (set_breakpoints) already cover that better anyway. Each entry is a small expression: \"vram(address)\" breaks on any VRAM read or write at that address; \"vram(address, \\\"R\\\")\" or \"vram(address, \\\"W\\\")\" restricts to reads or writes only; \"vram(start, end)\" or \"vram(start, end, \\\"R\\\"/\\\"W\\\")\" covers a range. \"vsync()\" breaks on every frame's vertical sync; \"vsync(frameNumber)\" breaks once, on that specific frame.")]
    public static async Task<string> SetHardwareBreakpoints(
        DapSession session,
        [Description("Expressions to set, e.g. [\"vram(0x1B000)\", \"vsync(120)\"]. Empty array clears all of them.")] string[] expressions)
    {
        var breakpoints = await session.SetHardwareBreakpoints(expressions);

        if (breakpoints.Count == 0)
            return expressions.Length == 0 ? "All hardware breakpoints cleared." : "No breakpoints were set.";

        return string.Join(Environment.NewLine, breakpoints.Select(b => b.Verified ? $"set: {b.Message}" : $"NOT set: {b.Message}"));
    }

    internal static string Summarize(IReadOnlyList<Breakpoint> breakpoints, string unverifiedSuffix)
    {
        var summary = breakpoints.Select(b => b.Verified
            ? $"line {b.Line}: verified"
            : $"line {b.Line}: NOT verified{(string.IsNullOrEmpty(b.Message) ? "" : $" ({b.Message})")}{unverifiedSuffix}");

        return string.Join(Environment.NewLine, summary);
    }
}
