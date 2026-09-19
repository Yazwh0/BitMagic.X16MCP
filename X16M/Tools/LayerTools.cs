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
}
