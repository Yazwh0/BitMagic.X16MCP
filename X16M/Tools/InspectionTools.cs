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

    // Measured against a real project: Globals/Kernal are tiny, CPU ~10K chars, but VERA hit
    // ~38K chars/1869 lines at depth 6 (sprite/palette-sized hardware scopes are wide, not just
    // deep). Conservative defaults plus a hard line cap keep one call from surprising a caller
    // that just asked for a scope by name without knowing its size.
    private const int DefaultMaxDepth = 3;
    private const int MaxLines = 300;

    [McpServerTool(Name = "get_variables", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lists every variable in a debug scope as an indented tree, e.g. 'Globals' for every variable the compiler knows about across the whole program (nested exactly as you'd type it into evaluate, e.g. a line \"NestedProc\" under \"MyProc\" under \"Main\" means Main:MyProc:NestedProc:someVar), 'Locals' for the current stack frame's variables, or any hardware scope (CPU, VERA, VERA Audio, VERA FX, Kernal, Display, I2C, SMC, UART, RTC, VIA, SD Card). Call this with no scopeName first to see the available scope names. Some hardware scopes (e.g. VERA) are large - the default depth is deliberately shallow and the result is capped at 300 lines; pass a smaller maxDepth to narrow it down or evaluate a specific path directly once you know it.")]
    public static async Task<string> GetVariables(
        DapSession session,
        [Description("Scope name, e.g. 'Globals' or 'Locals'. Omit to just list the available scope names.")] string? scopeName = null,
        [Description("Stack frame id from get_stack_trace. Only affects the 'Locals' scope - every other scope ignores it and a default of 0 is fine.")] int frameId = 0,
        [Description("How many levels deep to expand nested variables. Some scopes (e.g. VERA) are wide as well as deep, so even a shallow depth can be a lot of output.")] int maxDepth = DefaultMaxDepth)
    {
        var scopesResponse = await session.GetScopes(frameId);

        if (scopeName is null)
            return "Available scopes: " + string.Join(", ", scopesResponse.Scopes.Select(s => s.Name));

        var scope = scopesResponse.Scopes.FirstOrDefault(s => string.Equals(s.Name, scopeName, StringComparison.OrdinalIgnoreCase));
        if (scope is null)
            return $"No scope named '{scopeName}'. Available scopes: {string.Join(", ", scopesResponse.Scopes.Select(s => s.Name))}";

        if (scope.VariablesReference == 0)
            return $"{scope.Name}: (empty)";

        var lines = new List<string>();
        var truncated = !await AppendVariableTree(session, scope.VariablesReference, 0, maxDepth, lines);

        if (lines.Count == 0)
            return $"{scope.Name}: (empty)";

        if (truncated)
            lines.Add($"... truncated at {MaxLines} lines. Narrow down with a smaller maxDepth, or evaluate a specific path directly once you know it.");

        return string.Join(Environment.NewLine, lines);
    }

    // Returns false if it stopped early because MaxLines was hit.
    private static async Task<bool> AppendVariableTree(DapSession session, int variablesReference, int depth, int maxDepth, List<string> lines)
    {
        var response = await session.GetVariables(variablesReference);
        var indent = new string(' ', depth * 2);

        foreach (var v in response.Variables)
        {
            if (lines.Count >= MaxLines)
                return false;

            lines.Add($"{indent}{v.Name} = {v.Value}");

            if (v.VariablesReference != 0 && depth < maxDepth)
            {
                if (!await AppendVariableTree(session, v.VariablesReference, depth + 1, maxDepth, lines))
                    return false;
            }
        }

        return true;
    }

    [McpServerTool(Name = "evaluate", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Evaluates an expression in the debugger's expression language: named symbols (e.g. 'Main:MyProc:counter', see get_variables), registers, and arithmetic. To read a byte at a runtime-computed address that isn't a named symbol, use peek(address) - e.g. peek(0x9A04) reads Main RAM; peek(address, space) reads another space (same names as read_memory's memoryReference: 'vram', 'sdcard', 'sdcardblock', 'nvram', 'rambank_<N>', 'rombank_<N>').")]
    public static async Task<string> Evaluate(
        DapSession session,
        [Description("Expression to evaluate.")] string expression,
        [Description("Stack frame id to evaluate in (from get_stack_trace); omit for the top frame's default context.")] int? frameId = null)
    {
        var response = await session.Evaluate(expression, frameId);
        return response.Result;
    }

    // Space names shared verbatim across read_memory/write_memory/search_memory - keep the
    // wording consistent so callers can pattern-match between them.
    private const string MemorySpaceDescription =
        "Memory space to read/write, not an address: 'main' (CPU-addressable RAM/ROM - use this " +
        "for game state, tile data, sprite attributes etc., the same address space code and " +
        "disassembly use), 'vram' (VERA video RAM), 'sdcard', 'sdcardblock', 'nvram', " +
        "'rambank_<N>', or 'rombank_<N>'. The actual address goes in the separate address parameter.";

    [McpServerTool(Name = "disassemble", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Disassembles instructions in CPU-addressable memory (banking is resolved automatically from the address, like the 'main' space in read_memory) starting at a given address.")]
    public static async Task<string> Disassemble(
        DapSession session,
        [Description("Address to start disassembling from, as hex digits with an optional '0x' prefix, e.g. '0810' or '0x0810'. Do not use a '$' prefix here.")] string memoryReference,
        [Description("Number of instructions to disassemble.")] int instructionCount = 20)
    {
        var response = await session.Disassemble(memoryReference, instructionCount);

        var lines = response.Instructions.Select(i =>
            $"{i.Address}  {i.InstructionBytes,-12} {i.Instruction}");

        return string.Join(Environment.NewLine, lines);
    }

    // address is a string, not an int: a JSON number can't be written as "0x9A04" - a caller
    // that follows the hex examples in these descriptions literally would fail to even form a
    // valid tool call against an int-typed parameter, with no useful error surfacing (MCP
    // parameter-binding failures happen before McpException-based error handling ever runs).
    private const string AddressDescription = "Address within that space, as decimal (e.g. '39428') or hex with a '0x' or '$' prefix (e.g. '0x9A04' or '$9A04').";

    private static bool TryParseAddress(string input, out int address)
    {
        var trimmed = input.Trim();

        if (trimmed.StartsWith("$"))
            return int.TryParse(trimmed[1..], System.Globalization.NumberStyles.HexNumber, null, out address);

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out address);

        return int.TryParse(trimmed, out address);
    }

    [McpServerTool(Name = "read_memory", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads raw bytes from a memory space at a given address, returned as base64. To read a known address like $9A04 in Main RAM, use memoryReference 'main' with address '0x9A04' (or '39428').")]
    public static async Task<string> ReadMemory(
        DapSession session,
        [Description(MemorySpaceDescription)] string memoryReference,
        [Description(AddressDescription)] string address,
        [Description("Number of bytes to read.")] int count)
    {
        if (!TryParseAddress(address, out var addressValue))
            return $"Could not parse address '{address}'. " + AddressDescription;

        var response = await session.ReadMemory(memoryReference, count, addressValue);

        if (string.IsNullOrEmpty(response.Data) || response.UnreadableBytes >= count)
            return $"No data returned - either '{memoryReference}' isn't a recognised memory space, or address 0x{addressValue:X4} is out of range for it. " + MemorySpaceDescription;

        return $"address: {response.Address}, data (base64): {response.Data}";
    }

    [McpServerTool(Name = "write_memory", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Writes raw bytes to a memory space at a given address, to amend live state (e.g. poke a value to test a theory). Available even when attached to a session you don't own, since it amends state rather than controlling execution - unlike set_breakpoints/continue_execution/step_*.")]
    public static async Task<string> WriteMemory(
        DapSession session,
        [Description(MemorySpaceDescription)] string memoryReference,
        [Description(AddressDescription)] string address,
        [Description("Bytes to write (each 0-255), starting at that address.")] byte[] data)
    {
        if (!TryParseAddress(address, out var addressValue))
            return $"Could not parse address '{address}'. " + AddressDescription;

        var response = await session.WriteMemory(memoryReference, data, addressValue);

        if (response.BytesWritten == 0 && data.Length > 0)
            return $"Wrote nothing - either '{memoryReference}' isn't a recognised memory space, or address 0x{addressValue:X4} is out of range for it. " + MemorySpaceDescription;

        return $"Wrote {response.BytesWritten} of {data.Length} byte(s) at address {response.Offset}.";
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
