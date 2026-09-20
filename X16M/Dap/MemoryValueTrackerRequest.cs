using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "getMemoryValueLocations" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/MemoryValueTracker.cs). A "Cheat Engine" style value
/// scanner over the "main" address space: the first call (Locations empty/null) scans fresh for
/// ToFind; a follow-up call passes the previous response's Locations back to narrow the set down
/// against a new SearchType, comparing each location's current value to what it held last time.
/// </summary>
internal sealed class MemoryValueTrackerRequest : DebugRequestWithResponse<MemoryValueTrackerArguments, MemoryValueTrackerResponse>
{
    public MemoryValueTrackerRequest() : base("getMemoryValueLocations")
    {
    }
}

internal sealed class MemoryValueTrackerArguments : DebugRequestArguments
{
    public string SearchWidth { get; set; } = "";
    public uint ToFind { get; set; }
    public string SearchType { get; set; } = "";
    public MemoryValueDto[]? Locations { get; set; }
}

public sealed class MemoryValueTrackerResponse : ResponseBody
{
    public uint ToFind { get; set; }
    public string SearchType { get; set; } = "";
    public List<MemoryValueDto>? Locations { get; set; }
    public bool Stepping { get; set; }
}

/// <summary>
/// One matching location. <see cref="Location"/> is an opaque id, not a plain CPU address - it
/// encodes which RAM bank a banked match came from. Round-trip it into a follow-up FindMemoryValue
/// call; don't feed it to read_memory/write_memory.
/// </summary>
public sealed class MemoryValueDto
{
    public int Location { get; set; }
    public uint Value { get; set; }
}
