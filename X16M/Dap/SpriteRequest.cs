using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "spriteView" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/SpriteView.cs). That endpoint is multiplexed on
/// <see cref="SpriteRequestArguments.Command"/> (it also carries the VSCode sprite view's
/// "setDebugColours" highlight feature) - only "getSprites" is implemented here, the highlight
/// feature is a VSC-UI visual aid with nothing for an MCP tool to do with it.
/// </summary>
internal sealed class SpriteRequest : DebugRequestWithResponse<SpriteRequestArguments, SpriteRequestResponse>
{
    public SpriteRequest() : base("spriteView")
    {
    }
}

internal sealed class SpriteRequestArguments : DebugRequestArguments
{
    public string Command { get; set; } = "getSprites";
}

/// <summary>
/// One entry per sprite (property names/casing match X16D's wire format exactly, including the
/// "Pallette" spelling). <see cref="Display"/> is a base64 PNG cropped to just that sprite's own
/// pixels (<see cref="Width"/> by <see cref="Height"/>), not composited onto the screen.
/// </summary>
public sealed class SpriteDefinition
{
    public int Index { get; set; }
    public string Display { get; set; } = "";
    public uint Address { get; set; }
    public uint PalletteOffset { get; set; }
    public uint CollisionMask { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public uint Depth { get; set; }
    public bool HFlip { get; set; }
    public bool VFlip { get; set; }
    public uint Mode { get; set; }
}

public sealed class SpriteRequestResponse : ResponseBody
{
    public List<SpriteDefinition>? Sprites { get; set; }
}
