using System.ComponentModel;
using ModelContextProtocol.Server;
using X16M.Dap;

namespace X16M.Tools;

[McpServerToolType]
public static class BuildTools
{
    [McpServerTool(Name = "build_project", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Compiles a BitMagic project (.json) or a single .bmasm/.asm source file with X16D's --buildOnly mode - assembles it and reports any errors, without starting a debug session, loading a ROM, or running anything. Useful as a fast compile-check loop while writing bmasm: point this at the file, fix whatever it reports, repeat. Spawns a separate, short-lived X16D process each call and is entirely independent of any live debug session from launch_project/attach_to_session - safe to call at any time, even mid-session. Only available when X16M was configured with --x16d/X16D_PATH (a local X16D.exe to spawn); it can't run over a --x16d-host/--x16d-port TCP connection to an already-running X16D.")]
    public static async Task<string> BuildProject(
        DapSession session,
        [Description("Path to the .json project file, or a single .bmasm/.asm source file, to compile.")] string target,
        [Description("Base folder project-relative paths (outputFolder, binFolder, etc.) are resolved against. Defaults to the target file's own directory.")] string? buildFolder = null,
        [Description("Where the compiled program (.prg etc) is written - this is the actual build output. Especially worth setting when target is a bare source file rather than a .json project, since there's no project file to carry an outputFolder of its own otherwise; without this it defaults to a 'bin' folder under buildFolder. Overrides a .json project's own \"outputFolder\" too, if given.")] string? outputFolder = null,
        [Description("Where the template engine writes its own intermediate artifacts (compiled C# from bmasm's @(...) template code, generated .bmasm) - NOT the compiled program itself, see outputFolder for that. Rarely needs setting; defaults to a 'bin' folder under buildFolder. Overrides a .json project's own compileOptions.binFolder too, if given.")] string? binFolder = null)
    {
        var (exitCode, output) = await session.Build(target, buildFolder, outputFolder, binFolder);

        var status = exitCode == 0 ? "Build succeeded." : $"Build failed (exit code {exitCode}).";
        return string.IsNullOrWhiteSpace(output) ? status : $"{status}{Environment.NewLine}{output}";
    }
}
