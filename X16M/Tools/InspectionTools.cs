using System.ComponentModel;
using System.Globalization;
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
        "disassembly use), 'vram' (VERA video RAM), 'sdcard', 'sdcardblock', 'nvram', 'rambank', " +
        "or 'rombank'. The actual address goes in the separate address parameter; 'rambank'/'rombank' " +
        "also need the bank parameter.";

    private const string BankDescription = "Bank number - required when memoryReference is 'rambank' or 'rombank', ignored/unused for every other space.";

    // MemorySpaceResolver (server-side) still expects the bank folded into memoryReference as
    // e.g. "rambank_5" - that wire format doesn't need to change just because the tool-facing
    // shape now splits it into two parameters that are easier to discover and fill in separately.
    private static bool TryResolveMemoryReference(string memoryReference, int? bank, out string resolved, out string? error)
    {
        var space = memoryReference.Trim().ToLowerInvariant();

        if (space is "rambank" or "rombank")
        {
            if (bank is null)
            {
                resolved = "";
                error = $"memoryReference '{memoryReference}' needs a bank number - pass the bank parameter too.";
                return false;
            }

            resolved = $"{space}_{bank}";
            error = null;
            return true;
        }

        resolved = memoryReference;
        error = null;
        return true;
    }

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

    // address is a single concretely-typed string, not JsonElement: a schema with no "type" at
    // all (tried first) is non-standard, and a real tool-calling client needs a definite type to
    // know how to serialize the argument - a request that never even reaches our code (confirmed
    // via the DAP wire log: readMemory never once appears there across several failing rounds)
    // is consistent with the client being unable to form a call against a type-less schema at
    // all, not with anything our own parsing logic could catch. A plain string is something
    // every client can always produce (numbers stringify trivially), and we parse it ourselves.
    internal const string AddressDescription = "Address within that space, as decimal (e.g. \"39428\") or hex with a '0x' or '$' prefix (e.g. \"0x9A04\" or \"$9A04\").";

    internal static bool TryParseAddress(string address, out int value)
    {
        var trimmed = address.Trim();

        if (trimmed.StartsWith("$"))
            return int.TryParse(trimmed[1..], NumberStyles.HexNumber, null, out value);

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(trimmed[2..], NumberStyles.HexNumber, null, out value);

        return int.TryParse(trimmed, out value);
    }

    [McpServerTool(Name = "read_memory", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads raw bytes from a memory space at a given address, returned as base64. To read a known address like $9A04 in Main RAM, use memoryReference 'main' with address \"0x9A04\" (or \"39428\").")]
    public static async Task<string> ReadMemory(
        DapSession session,
        [Description(MemorySpaceDescription)] string memoryReference,
        [Description(AddressDescription)] string address,
        [Description("Number of bytes to read.")] int count,
        [Description(BankDescription)] int? bank = null)
    {
        if (!TryParseAddress(address, out var addressValue))
            return $"Could not parse address '{address}'. " + AddressDescription;

        if (!TryResolveMemoryReference(memoryReference, bank, out var resolvedReference, out var referenceError))
            return referenceError!;

        var response = await session.ReadMemory(resolvedReference, count, addressValue);

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
        [Description("Bytes to write (each 0-255), starting at that address.")] byte[] data,
        [Description(BankDescription)] int? bank = null)
    {
        if (!TryParseAddress(address, out var addressValue))
            return $"Could not parse address '{address}'. " + AddressDescription;

        if (!TryResolveMemoryReference(memoryReference, bank, out var resolvedReference, out var referenceError))
            return referenceError!;

        var response = await session.WriteMemory(resolvedReference, data, addressValue);

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

    [McpServerTool(Name = "get_palette", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Returns the current VERA palette: 256 entries, both as fully-resolved 8-bit RGBA colours (what's actually rendered) and as the raw 4-bit-per-channel values VERA itself stores in palette RAM.")]
    public static async Task<string> GetPalette(DapSession session)
    {
        var response = await session.GetPalette();

        if (response.DisplayPalette is null || response.DisplayPalette.Count == 0)
            return "No palette data returned.";

        var lines = new List<string> { "index  display (RGBA)     raw VERA (4-bit R,G,B)" };
        for (var i = 0; i < response.DisplayPalette.Count; i++)
        {
            var display = response.DisplayPalette[i];
            var raw = response.Palette != null && i < response.Palette.Count ? response.Palette[i] : null;

            lines.Add($"{i,3}    #{display.R:X2}{display.G:X2}{display.B:X2}{display.A:X2}    {(raw is null ? "-" : $"{raw.R:X1},{raw.G:X1},{raw.B:X1}")}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    [McpServerTool(Name = "find_memory_value", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Scans the \"main\" address space (all RAM banks) for a value, \"Cheat Engine\" style - the way to find where an unknown game variable (health, score, a counter) lives without knowing its address. First call with no locations to scan fresh for a value - this walks every RAM bank and takes roughly 30-60s regardless of which value or searchType you pick, so only call it once per hunt, not repeatedly. To narrow down, take the locations array that call returned, let the target run a bit (or change something in-game), then call again passing that same locations array back with a new searchType comparing each one's current value to what it held last time (e.g. \"Changed\", \"Gone Up\") - repeat until only the address you want is left; these narrowing calls only re-check the locations you pass in, so they're fast. A \"location\" in the result is an opaque id, not a plain CPU address; it round-trips into the next call but isn't valid input to read_memory/write_memory.")]
    public static async Task<string> FindMemoryValue(
        DapSession session,
        [Description("Value to search for (searchType \"Equal\"/\"Not Equal\"/etc.) - ignored when searchType is \"Changed\"/\"Not Changed\"/\"Gone Up\"/\"Gone Down\", which compare against each location's previous value instead.")] uint value,
        [Description("\"Equal\", \"Not Equal\", \"Less Than\", \"Greater Than\" (all compare against the value parameter), or - only valid on a narrowing call that passes locations back - \"Changed\", \"Not Changed\", \"Gone Up\", \"Gone Down\" (compare against each location's previous value).")] string searchType,
        [Description("\"Byte\" (0-255) or \"Word\" (0-65535, little-endian).")] string searchWidth,
        [Description("Omit for a fresh scan. On a narrowing call, pass back exactly the locations array the previous call returned.")] MemoryValueDto[]? locations = null)
    {
        if (searchType is not ("Equal" or "Not Equal" or "Less Than" or "Greater Than" or "Changed" or "Not Changed" or "Gone Up" or "Gone Down"))
            return $"Unknown searchType '{searchType}'. Use \"Equal\", \"Not Equal\", \"Less Than\", \"Greater Than\", \"Changed\", \"Not Changed\", \"Gone Up\", or \"Gone Down\".";

        if (searchWidth is not ("Byte" or "Word"))
            return $"Unknown searchWidth '{searchWidth}'. Use \"Byte\" or \"Word\".";

        var response = await session.FindMemoryValue(value, searchType, searchWidth, locations);

        if (response.Locations is null || response.Locations.Count == 0)
            return "No matches found.";

        if (response.Locations.Count > 200)
            return $"{response.Locations.Count} matches - too many to be useful yet. Narrow down further before inspecting individual locations.";

        return $"{response.Locations.Count} match(es):" + Environment.NewLine +
            string.Join(Environment.NewLine, response.Locations.Select(l => $"location {l.Location}: value {l.Value}"));
    }
}
