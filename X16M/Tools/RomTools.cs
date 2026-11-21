using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using X16M.Rom;

namespace X16M.Tools;

[McpServerToolType]
public static class RomTools
{
    [McpServerTool(Name = "fetch_rom", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Downloads the latest Commander X16 ROM (rom.bin) from the official X16Community/x16-rom GitHub releases, using the fetch-rom script bundled with X16M. Use this when launch_project warns that no ROM was found. By default it saves to the user's BitMagic folder (%LOCALAPPDATA%\\BitMagic on Windows, ~/.local/share/BitMagic on Linux), which launch_project falls back to automatically and the standalone X16E emulator also uses, so nothing else needs configuring. Overwrites any rom.bin already there, so it doubles as an update to the latest ROM. Needs internet access, plus curl and tar on Windows (both ship with Windows 10 1803+), or curl and unzip on Linux. Doesn't need or affect a debug session.")]
    public static async Task<string> FetchRom(
        [Description("Folder to save rom.bin into. Defaults to the user's BitMagic folder, which is the right choice unless you need the ROM somewhere specific; a ROM saved elsewhere is only used if a project's romFile or the BITMAGIC_ROM environment variable points at it.")] string? destinationFolder = null)
    {
        var destination = string.IsNullOrWhiteSpace(destinationFolder)
            ? RomLocator.UserRomFolder
            : Path.GetFullPath(destinationFolder);

        int exitCode;
        string output;
        try
        {
            (exitCode, output) = await RomLocator.Fetch(destination, TimeSpan.FromMinutes(2));
        }
        catch (FileNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }

        var status = exitCode == 0
            ? $"ROM fetched to '{Path.Combine(destination, "rom.bin")}'."
            : $"ROM fetch failed (exit code {exitCode}).";

        return string.IsNullOrWhiteSpace(output) ? status : $"{status}{Environment.NewLine}{output}";
    }
}
