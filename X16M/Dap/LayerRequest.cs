using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "getLayers" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/LayerView.cs), which isn't part of standard DAP so has no
/// type already available from the shared protocol package the way Disassemble/ReadMemory do.
/// </summary>
internal sealed class LayerRequest : DebugRequestWithResponse<LayerRequestArguments, LayerRequestResponse>
{
    public LayerRequest() : base("getLayers")
    {
    }
}

internal sealed class LayerRequestArguments : DebugRequestArguments
{
}

/// <summary>
/// Six PNG images, base64-encoded, in VERA compositing order: background, sprites (z=1), layer 0,
/// sprites (z=2), layer 1, sprites (z=3). Rendered from whatever the emulator's display buffer
/// currently holds: the beam only advances alongside executed CPU cycles, so while paused
/// mid-frame this can be a partial/torn frame rather than a complete one.
/// </summary>
public sealed class LayerRequestResponse : ResponseBody
{
    public List<string> Display { get; set; } = new();
}
