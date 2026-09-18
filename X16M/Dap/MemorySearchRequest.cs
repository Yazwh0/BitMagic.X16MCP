using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "searchMemory" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/MemorySearch.cs), which isn't part of standard DAP so has
/// no type already available from the shared protocol package the way Disassemble/ReadMemory do.
/// </summary>
internal sealed class MemorySearchRequest : DebugRequestWithResponse<MemorySearchArguments, MemorySearchResponse>
{
    // Args's setter is private (populated by the base constructor via `new TArgs()`), so a
    // caller can't assign Args itself - only mutate the properties of the instance it already got.
    public MemorySearchRequest() : base("searchMemory")
    {
    }
}

internal sealed class MemorySearchArguments : DebugRequestArguments
{
    public string MemoryReference { get; set; } = "";
    public string Pattern { get; set; } = ""; // base64-encoded byte pattern
    public bool CaseInsensitive { get; set; }
    public int MaxResults { get; set; } = 500;
}

/// <summary>Byte offsets within the resolved memory space where the pattern was found.</summary>
public sealed class MemorySearchResponse : ResponseBody
{
    public List<int> Matches { get; set; } = new();
    public bool Truncated { get; set; }
    public int SpaceLength { get; set; }
}
