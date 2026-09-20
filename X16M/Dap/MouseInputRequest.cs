using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "mouseInput" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/MouseInput.cs). Injects a single mouse sample (movement
/// delta plus current button state) into the emulator's own SMC mouse buffer - the exact same
/// SmcBuffer.PushMouse call the standalone X16E window makes from real OS mouse events. The
/// X16's PS/2-style mouse protocol is relative, not absolute - there's no "move to X,Y".
/// </summary>
internal sealed class MouseInputRequest : DebugRequestWithResponse<MouseInputRequestArguments, MouseInputRequestResponse>
{
    public MouseInputRequest() : base("mouseInput")
    {
    }
}

internal sealed class MouseInputRequestArguments : DebugRequestArguments
{
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public bool Left { get; set; }
    public bool Right { get; set; }
    public bool Middle { get; set; }
}

public sealed class MouseInputRequestResponse : ResponseBody
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
