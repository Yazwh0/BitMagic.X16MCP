using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class LayerTools
{
    // X16D's getLayers response is indexed the same way its own VSCode layer view labels it
    // (BitMagic.VSC/.../layerView/layerView.ts): compositing order, not layer/sprite number.
    private static readonly string[] LayerNames =
    [
        "Background", "Sprite 1", "Layer 0", "Sprite 2", "Layer 1", "Sprite 3",
    ];

    [McpServerTool(Name = "get_layers", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Returns the current VERA display as six separate images in compositing order (Background, Sprite 1, Layer 0, Sprite 2, Layer 1, Sprite 3), matching the VSCode extension's own layer view. Rendered from whatever the display buffer currently holds: while paused mid-frame this can be a partial/torn image rather than a complete one, since the beam only advances alongside executed CPU cycles.")]
    public static async Task<CallToolResult> GetLayers(DapSession session)
    {
        var response = await session.GetLayers();
        var result = new CallToolResult();

        for (var i = 0; i < response.Display.Count && i < LayerNames.Length; i++)
        {
            result.Content.Add(new TextContentBlock { Text = LayerNames[i] });
            result.Content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(response.Display[i]), "image/png"));
        }

        return result;
    }

    private static readonly string[] RenderModes = ["beam", "finishFrame", "fromState"];

    [McpServerTool(Name = "get_screen", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Returns the VERA display as a single 640x480 image, with all six layers blended together the way the X16E window shows them. Use get_layers to see what each layer contributes. By default it's the display as drawn so far: while paused mid-frame the lines below the beam are still from the previous frame. These change that while paused, without running the CPU or changing the machine: render_mode 'finishFrame' draws the rest of the frame to vsync from VERA as it is now, 'fromState' draws the whole frame from VERA as it is now (so VRAM or register changes made while paused show straight away). x and y instead draw on from where it's paused to that beam position (0-799, 0-524, wrapping into the next frame if it's behind the beam), to see what VERA would have drawn by then. None of them show changes the program would make later in the frame, such as raster effects. While running it's always the display as it is.")]
    public static async Task<CallToolResult> GetScreen(
        DapSession session,
        [Description("'beam' (default), 'finishFrame' or 'fromState'.")] string? render_mode = null,
        [Description("Beam x to draw on to while paused, 0-799. Needs y, and can't be used with render_mode.")] int? x = null,
        [Description("Beam y to draw on to while paused, 0-524. Needs x.")] int? y = null)
    {
        var result = new CallToolResult();
        string? error = null;

        if (render_mode != null && !RenderModes.Contains(render_mode))
            error = $"Unknown render_mode '{render_mode}', use {string.Join(", ", RenderModes)}.";
        else if ((x == null) != (y == null))
            error = "x and y need to be given together.";
        else if (x != null && render_mode != null)
            error = "Use either render_mode or x and y, not both.";
        else if (x is < 0 or > 799 || y is < 0 or > 524)
            error = "x must be 0-799 and y 0-524.";

        if (error != null)
        {
            result.IsError = true;
            result.Content.Add(new TextContentBlock { Text = error });
            return result;
        }

        var response = await session.GetScreen(render_mode, x, y);
        var wanted = x != null ? $"drawn to {x}, {y}" : render_mode;

        if (wanted != null && response.RenderMode == "beam" && render_mode != "beam")
            result.Content.Add(new TextContentBlock { Text = $"The target is running, so this is the display as it is rather than {wanted}." });

        result.Content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(response.Display), "image/png"));

        return result;
    }
}
