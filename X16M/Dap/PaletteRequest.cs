using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "bm_palette" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/PaletteRequest.cs).
/// </summary>
internal sealed class PaletteRequest : DebugRequestWithResponse<PaletteRequestArguments, PaletteRequestResponse>
{
    public PaletteRequest() : base("bm_palette")
    {
    }
}

internal sealed class PaletteRequestArguments : DebugRequestArguments
{
}

/// <summary>Fully-resolved 8-bit RGBA colour, as actually rendered - the renderer's expansion of the raw 4-bit VERA palette entries.</summary>
public sealed class DisplayPaletteEntry
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; }
}

/// <summary>One raw VERA palette RAM entry - 4 bits per channel, straight from VRAM, as the hardware itself stores it.</summary>
public sealed class VeraPaletteItem
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
}

public sealed class PaletteRequestResponse : ResponseBody
{
    public List<DisplayPaletteEntry>? DisplayPalette { get; set; }
    public List<VeraPaletteItem>? Palette { get; set; }
}
