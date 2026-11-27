# X16M

Lets an AI agent, Claude Code or any other MCP client, drive a BitMagic debug session itself:
launching a project, setting breakpoints, stepping, and inspecting memory, the same way VSCode
does via `BitMagic.X16Debugger`.

![Claude Code driving a BitMagic debug session through X16M](https://bitmagic.org/Images/MCPExample.png)

## What it's for

Normally you run the debugger and describe what you see to an agent. X16M lets the agent drive
the session directly instead. Ask it in plain English to launch a project, set a breakpoint on a
line, and report back what a variable holds once it's hit; it picks the right calls itself.

X16M doesn't modify or depend on the debugger's internals. It speaks the Debug Adapter Protocol
(DAP) to `X16D`, either spawning it as a child process or connecting over TCP to one already
running with `--dapport`, and re-exposes a subset of DAP as MCP tools.

## Install

```bash
dotnet tool install -g BitMagic.X16M
```

This bundles a matching build of `X16D` for your platform, Windows or Linux, so there's nothing
else to configure beyond a [ROM](https://bitmagic.org/emulator/rom). For an early build, add
`--prerelease`.

On Linux, the bundled `X16D` needs glibc 2.34 or later (Ubuntu 22.04, Debian 12 or newer),
OpenAL, OpenGL and an X11 or Wayland display; see the
[Linux prerequisites](https://bitmagic.org/debugger/#linux-prerequisites) for the full list.

A .NET tool doesn't auto-update. To pick up a newer version later, run:

```bash
dotnet tool update -g BitMagic.X16M
```

## A ROM

The X16 ROM contains licensed code, so it isn't included; X16M can fetch one for you. Its
`fetch_rom` tool downloads the latest release from the official
[x16-rom repository](https://github.com/X16Community/x16-rom) and saves `rom.bin` to your
BitMagic folder: `%LOCALAPPDATA%\BitMagic` on Windows, `~/.local/share/BitMagic` on Linux.
`launch_project` uses a ROM there automatically, so just ask your agent to fetch one. It needs
`curl` and `tar` on Windows (both ship with Windows 10 1803 and later), or `curl` and `unzip`
on Linux.

The same scripts sit next to `X16M` as `fetch-rom.bat` and `fetch-rom.sh` if you'd rather run
them yourself; pass a folder as the first argument to save `rom.bin` somewhere else.

`launch_project` looks for a ROM in this order:

1. a `romFile` set in the project file
2. `rom.bin` next to the bundled `X16D`
3. the `BITMAGIC_ROM` environment variable, either the file or the folder containing it
4. your BitMagic folder

Without one, `launch_project` returns a clear warning suggesting `fetch_rom`, rather than
failing silently.

## Registering it with an MCP client

`dotnet tool install -g` puts `x16m` on your PATH, so there's no executable to hunt down.

For Claude Code, register it with the CLI rather than a project's `.mcp.json`, so it isn't tied
to any one repo:

```bash
claude mcp add x16m --scope user -- x16m
```

`--scope user` makes it available from every project, in every session, on this machine; that's
the one to reach for unless you have a specific reason not to. `--scope local` only takes effect
in sessions launched from the exact directory you were standing in when you ran the command, so
a session started anywhere else, even after a full restart, won't see it.

Either way the registration lives in your own `~/.claude.json`, never in a project file.

Other MCP clients typically want the equivalent of a `.mcp.json` entry:

```json
{
  "mcpServers": {
    "x16m": {
      "command": "x16m"
    }
  }
}
```

If a previous registration shows a stale path, a "Conflicting scopes" warning, or the tools
don't show up after a full restart, remove it and re-add: `claude mcp remove x16m --scope
<scope>`, then the command above.

## Available tools

Standard DAP is covered, plus X16-specific tools for VERA layers, sprites and CPU history.

| Tool | Description |
| ---- | ----------- |
| `launch_project(projectPath, breakpoints?, stopOnEntry?, muteAudio?, workspaceFolder?, headless?)` | Launches a project's `.json` file, or a `.bmasm` file directly, and waits for its initial stop before returning. Pass `breakpoints` here rather than a follow-up `set_breakpoints` call: some targets finish in well under a second, faster than a separate tool call can land. Audio is muted by default (`muteAudio: true`); this only silences the output, emulation is unaffected. Relative paths in the project file resolve as they do in VSCode, against the nearest folder above it with a `.vscode` folder (else its own folder); `workspaceFolder` overrides that. `headless: true` runs without the emulator window (the emulator still renders, so `get_screen` works); it needs X16M to start `X16D` itself, so not with `--x16d-host`/`--x16d-port`. |
| `set_breakpoints(file, lines[])` | Sets the full set of breakpoints for a file, replacing any previously set there. |
| `get_breakpoints()` | Reports every breakpoint set this session, source, instruction and hardware, and whether each is verified. `X16D` verifies a breakpoint once the code it's for loads, which can happen after `set_breakpoints` or `launch_project` already returned. |
| `continue_execution()` | Resumes a paused session. |
| `step_over()` | Steps over the current line. |
| `step_into()` | Steps into a call on the current line. |
| `step_out()` | Steps out of the current function. |
| `get_stack_trace()` | Returns the current call stack. |
| `evaluate(expression)` | Evaluates an expression in the current scope. |
| `disassemble(memoryReference, instructionCount)` | Disassembles instructions from a memory location. |
| `read_memory(memoryReference, count)` | Reads a block of memory. |
| `get_layers()` | Returns the current VERA display as six images, one per compositing layer (background, layer 0, layer 1, and sprites at each of their three depth slots). While paused mid-frame this can be a partial image rather than a complete one, since the beam only advances alongside executed CPU cycles. |
| `get_sprites()` | Returns each sprite VERA currently has enabled (depth != 0), with its attributes and its own cropped image. VERA has a fixed table of 128 sprite slots; disabled ones are omitted rather than returned as 128 mostly-empty entries. |
| `get_cpu_history(count?)` | Returns the most recently executed CPU instructions, most recent first, with register state, flags and source file/line where known. Useful for seeing how execution actually reached the current stop, not just where it is now. |
| `disconnect()` | Ends the debug session. |
| `fetch_rom(destinationFolder?)` | Downloads the latest X16 ROM to your BitMagic folder (or `destinationFolder`), overwriting any `rom.bin` already there. Doesn't need a debug session. |

## Example

Once X16M is registered, you don't call its tools directly. You ask your agent in plain English,
and it picks the right tools for you.

> **You:** Launch the project at `project.json` with a breakpoint on the line that increments
> `counter`.
>
> **Agent:** Launched it with that breakpoint in place; `inc counter` is on line 11 of
> `main.bmasm`. It's already verified, and the target's paused at the start.

> **You:** Continue, and tell me what `counter` is.
>
> **Agent:** Hit the breakpoint. `counter` is `0x01`.

The `BitMagic.X16MCP` repository has a
[worked example](https://github.com/Yazwh0/BitMagic.X16MCP/tree/main/example) with a minimal
project and a full walkthrough, a good first thing to try after registering X16M.

Full source and build-from-source instructions: https://github.com/Yazwh0/BitMagic.X16MCP. More
at https://bitmagic.org/mcpagent.
