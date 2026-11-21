using System.Diagnostics;
using System.Text;

namespace X16M.Rom;

// Where X16M looks for an X16 ROM, and how it fetches one. Mirrors X16E's own lookup (see
// BitMagic.X16Emulator/X16E/Program.cs) so a ROM fetched by either is found by both.
public static class RomLocator
{
    public const string RomEnvironmentVariable = "BITMAGIC_ROM";

    // %LOCALAPPDATA%\BitMagic on Windows, ~/.local/share/BitMagic on Linux - the same folder
    // X16E --fetch-rom and the bundled fetch-rom scripts save to by default.
    public static string UserRomFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitMagic");

    public static string UserRomPath => Path.Combine(UserRomFolder, "rom.bin");

    // True if X16D (started in workingDirectory) will find a ROM on its own: rom.bin in its
    // working directory, or BITMAGIC_ROM pointing at the file or the folder containing it.
    public static bool X16DFindsRomUnaided(string workingDirectory)
    {
        if (File.Exists(Path.Combine(workingDirectory, "rom.bin")))
            return true;

        var env = Environment.GetEnvironmentVariable(RomEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(env) && (File.Exists(env) || File.Exists(Path.Combine(env, "rom.bin")));
    }

    // The ROM to hand X16D via BITMAGIC_ROM when it wouldn't find one itself, or null if it
    // either doesn't need help or there's nothing to give it. Always a file path rather than
    // a folder: X16D's folder handling appends a hardcoded '\rom.bin', which doesn't work on Linux.
    public static string? RomToPassToX16D(string workingDirectory)
    {
        if (X16DFindsRomUnaided(workingDirectory))
            return null;

        return File.Exists(UserRomPath) ? UserRomPath : null;
    }

    public static string ScriptPath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fetch-rom.bat" : "fetch-rom.sh");

    // Runs the bundled fetch-rom script, which downloads the latest release from
    // X16Community/x16-rom and saves rom.bin into destinationFolder.
    public static async Task<(int exitCode, string output)> Fetch(string destinationFolder, TimeSpan timeout)
    {
        var script = ScriptPath;
        if (!File.Exists(script))
            throw new FileNotFoundException($"The ROM fetch script wasn't found at '{script}'. It ships alongside X16M; reinstall X16M, or download a ROM manually (see https://bitmagic.org/emulator/rom).");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(script);
        }
        else
        {
            // Run through bash rather than directly: zip/nupkg extraction doesn't preserve the
            // executable bit, so the script may well not be marked executable.
            psi.FileName = "bash";
            psi.ArgumentList.Add(script);
        }
        psi.ArgumentList.Add(destinationFolder);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the ROM fetch script.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, $"The ROM fetch script didn't finish within {timeout.TotalSeconds:0} seconds and was stopped.");
        }

        var output = new StringBuilder((await stdoutTask).Trim());
        var stderr = (await stderrTask).Trim();
        if (stderr.Length > 0)
        {
            if (output.Length > 0)
                output.AppendLine();
            output.Append(stderr);
        }

        return (process.ExitCode, output.ToString());
    }
}
