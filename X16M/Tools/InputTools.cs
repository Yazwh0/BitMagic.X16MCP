using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class InputTools
{
    [McpServerTool(Name = "send_key", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Sends a single keyboard event to the running target, exactly as a real key press/release would arrive - one event per call, not a batch. To type a character, send it down then up (send_key twice); hold a modifier across other keys by sending it down, sending the other keys, then sending it up. Available even when attached to a session you don't own, since it amends emulator state rather than controlling execution - unlike set_breakpoints/continue_execution/step_*.")]
    public static async Task<string> SendKey(
        DapSession session,
        [Description("Key name, matching Silk.NET.Input.Key, e.g. \"A\", \"Number1\", \"Enter\", \"Space\", \"ShiftLeft\", \"ControlLeft\", \"AltLeft\", \"Up\"/\"Down\"/\"Left\"/\"Right\", \"F1\"-\"F12\", \"Backspace\", \"Escape\", \"Tab\", \"CapsLock\", \"Keypad0\"-\"Keypad9\".")] string key,
        [Description("true for key down (press), false for key up (release).")] bool down)
    {
        var response = await session.SendKey(key, down);

        return response.Success
            ? $"Sent key {key} {(down ? "down" : "up")}."
            : $"Failed to send key {key}: {response.Error}";
    }

    [McpServerTool(Name = "send_mouse", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Sends a single mouse sample to the running target: a movement delta plus the mouse's current button state, exactly as a real mouse would report one - one sample per call, not a batch. The X16's mouse is relative (PS/2-style), not absolute, so there's no \"move to X,Y\" - only \"move by this much from wherever the cursor already is\". To click, send the button held (deltaX/deltaY 0 is fine) then send it released. Available even when attached to a session you don't own, since it amends emulator state rather than controlling execution - unlike set_breakpoints/continue_execution/step_*.")]
    public static async Task<string> SendMouse(
        DapSession session,
        [Description("Horizontal movement since the last sample, in pixels. Positive is right.")] int deltaX = 0,
        [Description("Vertical movement since the last sample, in pixels. Positive is down.")] int deltaY = 0,
        [Description("Whether the left button is currently held down.")] bool left = false,
        [Description("Whether the right button is currently held down.")] bool right = false,
        [Description("Whether the middle button is currently held down.")] bool middle = false)
    {
        var response = await session.SendMouse(deltaX, deltaY, left, right, middle);

        return response.Success
            ? $"Sent mouse: delta ({deltaX}, {deltaY}), buttons [{string.Join(", ", new[] { left ? "left" : null, right ? "right" : null, middle ? "middle" : null }.Where(b => b != null))}]."
            : $"Failed to send mouse input: {response.Error}";
    }
}
