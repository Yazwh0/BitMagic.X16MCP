using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "getHistory" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/HistoryView.cs). That endpoint also supports "getall"
/// (the entire configured history buffer, which can be huge) and "reset" (clears it); only
/// "history" (the most recent page, up to 1024 instructions) is used here, most-recent-first.
/// </summary>
internal sealed class HistoryRequest : DebugRequestWithResponse<HistoryRequestArguments, HistoryRequestResponse>
{
    public HistoryRequest() : base("getHistory")
    {
    }
}

internal sealed class HistoryRequestArguments : DebugRequestArguments
{
    public string Message { get; set; } = "history";
    public int Index { get; set; } = 0;
}

/// <summary>One executed instruction (property names match X16D's wire format exactly).</summary>
public sealed record HistoryItem(
    string Proc, string OpCode, string RawParameter, int RamBank, int RomBank, int Pc,
    int A, int X, int Y, int Sp, string Flags, string SourceFile, int LineNumber, ulong Clock);

public sealed class HistoryRequestResponse : ResponseBody
{
    public List<HistoryItem> HistoryItems { get; set; } = new();
    public bool More { get; set; }
    public int Index { get; set; }
}
