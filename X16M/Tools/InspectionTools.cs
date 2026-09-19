using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class InspectionTools
{
    [McpServerTool(Name = "get_stack_trace", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Returns the current call stack for a thread, with source file/line for each frame where known.")]
    public static async Task<string> GetStackTrace(DapSession session, int threadId = 1, int startFrame = 0, int levels = 20)
    {
        var response = await session.GetStackTrace(threadId, startFrame, levels);

        var frames = response.StackFrames.Select(f =>
            $"#{f.Id} {f.Name} — {(f.Source is null ? "<no source>" : f.Source.Path)}:{f.Line}");

        return string.Join(Environment.NewLine, frames);
    }

    [McpServerTool(Name = "evaluate", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Evaluates an expression in the debugger's expression language (registers, symbols, memory, etc.) in the context of a stack frame.")]
    public static async Task<string> Evaluate(
        DapSession session,
        [Description("Expression to evaluate.")] string expression,
        [Description("Stack frame id to evaluate in (from get_stack_trace); omit for the top frame's default context.")] int? frameId = null)
    {
        var response = await session.Evaluate(expression, frameId);
        return response.Result;
    }

    [McpServerTool(Name = "disassemble", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Disassembles instructions starting at a memory reference.")]
    public static async Task<string> Disassemble(
        DapSession session,
        [Description("Memory reference to start disassembling from, e.g. an address like '0x0810' or an expression the target understands.")] string memoryReference,
        [Description("Number of instructions to disassemble.")] int instructionCount = 20)
    {
        var response = await session.Disassemble(memoryReference, instructionCount);

        var lines = response.Instructions.Select(i =>
            $"{i.Address}  {i.InstructionBytes,-12} {i.Instruction}");

        return string.Join(Environment.NewLine, lines);
    }

    [McpServerTool(Name = "read_memory", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads raw bytes starting at a memory reference, returned as base64 (per DAP's readMemory response).")]
    public static async Task<string> ReadMemory(
        DapSession session,
        [Description("Memory reference to start reading from, e.g. an address like '0x0810'.")] string memoryReference,
        [Description("Number of bytes to read.")] int count)
    {
        var response = await session.ReadMemory(memoryReference, count);
        return $"address: {response.Address}, data (base64): {response.Data}";
    }

    [McpServerTool(Name = "write_memory", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Writes raw bytes starting at a memory reference, to amend live state (e.g. poke a value to test a theory). Available even when attached to a session you don't own, since it amends state rather than controlling execution - unlike set_breakpoints/continue_execution/step_*.")]
    public static async Task<string> WriteMemory(
        DapSession session,
        [Description("Memory reference to start writing to, e.g. an address like '0x0810'.")] string memoryReference,
        [Description("Bytes to write (each 0-255), starting at that address.")] byte[] data)
    {
        var response = await session.WriteMemory(memoryReference, data);
        return $"Wrote {response.BytesWritten} of {data.Length} byte(s) at offset {response.Offset}.";
    }

    [McpServerTool(Name = "search_memory", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Searches a whole memory space for a byte pattern or text string, returning matching offsets. Runs server-side, so it's safe to use on large spaces like the SD card image without transferring the data.")]
    public static async Task<string> SearchMemory(
        DapSession session,
        [Description("Memory space to search: 'main', 'vram', 'nvram', 'sdcard', 'rambank_<N>', or 'rombank_<N>'.")] string memoryReference,
        [Description("Text to search for (case-insensitive), or hex bytes prefixed with $ or 0x, e.g. 'DEADBEEF' or 'DE AD BE EF'.")] string pattern,
        [Description("Maximum number of matches to return.")] int maxResults = 100)
    {
        var (patternBase64, caseInsensitive) = ParseSearchPattern(pattern);
        if (patternBase64 is null)
            return "Could not parse the search pattern.";

        var response = await session.SearchMemory(memoryReference, patternBase64, caseInsensitive, maxResults);

        if (response.Matches.Count == 0)
            return "No matches found.";

        var summary = response.Truncated
            ? $"Showing the first {response.Matches.Count} matches (more exist):"
            : $"{response.Matches.Count} match(es):";

        return summary + Environment.NewLine + string.Join(Environment.NewLine, response.Matches.Select(offset => $"0x{offset:X4}"));
    }

    // A leading $ or 0x means hex bytes (whitespace between pairs is fine); anything else is a
    // literal, case-insensitive text search.
    private static (string? patternBase64, bool caseInsensitive) ParseSearchPattern(string pattern)
    {
        var trimmed = pattern.Trim();
        if (trimmed.Length == 0)
            return (null, false);

        var isHex = trimmed.StartsWith("$") || trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (isHex)
        {
            var hex = (trimmed.StartsWith("$") ? trimmed[1..] : trimmed[2..]).Replace(" ", "");
            if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
                return (null, false);

            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

            return (Convert.ToBase64String(bytes), false);
        }

        return (Convert.ToBase64String(System.Text.Encoding.Latin1.GetBytes(trimmed)), true);
    }
}
