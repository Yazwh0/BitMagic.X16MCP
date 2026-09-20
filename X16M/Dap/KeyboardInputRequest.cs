using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace X16M.Dap;

/// <summary>
/// Client-side mirror of X16D's "keyboardInput" custom DAP request
/// (BitMagic.X16Debugger/CustomMessage/KeyboardInput.cs). Injects a single key event into the
/// emulator's own SMC keyboard buffer - the exact same SmcBuffer.KeyDown/KeyUp calls the
/// standalone X16E window makes from real OS key events.
/// </summary>
internal sealed class KeyboardInputRequest : DebugRequestWithResponse<KeyboardInputRequestArguments, KeyboardInputRequestResponse>
{
    public KeyboardInputRequest() : base("keyboardInput")
    {
    }
}

internal sealed class KeyboardInputRequestArguments : DebugRequestArguments
{
    // Name of a Silk.NET.Input.Key value, e.g. "A", "Enter", "ShiftLeft", "F5".
    public string Key { get; set; } = "";
    public bool Down { get; set; }
}

public sealed class KeyboardInputRequestResponse : ResponseBody
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
