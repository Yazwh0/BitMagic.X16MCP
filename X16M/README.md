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

## A ROM

Place it as `rom.bin` next to the bundled `X16D`, or point the `BITMAGIC_ROM` environment
variable at the file (or the folder containing it). Without one, `launch_project` returns a
clear warning rather than failing silently.

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

This first slice covers standard DAP only. X16-specific requests, sprites, palette, layers, CPU
history and the CPU profiler, aren't wired up yet.

| Tool | Description |
| ---- | ----------- |
| `launch_project(projectPath, breakpoints?)` | Launches a project's `.json` file, or a `.bmasm` file directly, and waits for its initial stop before returning. Pass `breakpoints` here rather than a follow-up `set_breakpoints` call: some targets finish in well under a second, faster than a separate tool call can land. |
| `set_breakpoints(file, lines[])` | Sets the full set of breakpoints for a file, replacing any previously set there. |
| `get_breakpoints()` | Reports the current verification state of every breakpoint set so far. `X16D` verifies a breakpoint once its file actually loads, which can happen after `set_breakpoints` or `launch_project` already returned. |
| `continue_execution()` | Resumes a paused session. |
| `step_over()` | Steps over the current line. |
| `step_into()` | Steps into a call on the current line. |
| `step_out()` | Steps out of the current function. |
| `get_stack_trace()` | Returns the current call stack. |
| `evaluate(expression)` | Evaluates an expression in the current scope. |
| `disassemble(memoryReference, instructionCount)` | Disassembles instructions from a memory location. |
| `read_memory(memoryReference, count)` | Reads a block of memory. |
| `disconnect()` | Ends the debug session. |

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
