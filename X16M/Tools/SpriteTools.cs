using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class SpriteTools
{
    [McpServerTool(Name = "get_sprites", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Returns each sprite VERA currently has enabled (depth != 0), with its attributes and its own cropped image. VERA has a fixed table of 128 sprite slots; disabled ones (depth 0, not displayed) are omitted rather than returned as 128 mostly-empty entries.")]
    public static async Task<CallToolResult> GetSprites(DapSession session)
    {
        var response = await session.GetSprites();
        var result = new CallToolResult();
        var sprites = response.Sprites ?? [];

        foreach (var s in sprites.Where(s => s.Depth != 0))
        {
            var flips = string.Join(", ", new[] { s.HFlip ? "h-flip" : null, s.VFlip ? "v-flip" : null }.Where(f => f is not null));
            var text = $"Sprite {s.Index}: {s.Width}x{s.Height} at ({s.X},{s.Y}), {s.Mode}bpp, depth {s.Depth}, " +
                       $"address 0x{s.Address:x}, palette offset {s.PalletteOffset}, collision mask 0x{s.CollisionMask:x}" +
                       (flips.Length > 0 ? $", {flips}" : "");

            result.Content.Add(new TextContentBlock { Text = text });
            result.Content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(s.Display), "image/png"));
        }

        if (result.Content.Count == 0)
            result.Content.Add(new TextContentBlock { Text = "No sprites are currently enabled (all 128 slots have depth 0)." });

        return result;
    }
}
