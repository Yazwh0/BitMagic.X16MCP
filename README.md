# X16M

An MCP server that lets an AI agent drive a BitMagic X16 debug session (breakpoints, stepping,
stack/memory inspection) the same way VS Code does via `BitMagic.X16Debugger`.

X16M does not modify or depend on the debugger's internals. It speaks DAP to X16D either by
spawning it as a plain child process (stdin/stdout, exactly as VS Code does) or by connecting
over TCP to one already running with `--dapport` (so X16D can be started and debugged
separately, e.g. under the Visual Studio debugger) - and re-exposes a subset of the DAP protocol
as MCP tools.

This is currently a plain, gitignored folder in the BitMagic superproject (not wired into
`BitMagic.sln`) — it's expected to move into its own submodule/repo later.

## Building

```
dotnet build BitMagic.X16MCP/X16M
```

## Running

X16M needs to know how to reach X16D. It resolves this in order:

1. `--x16d-host`/`--x16d-port` (or `X16D_HOST`/`X16D_PORT`) — connect over TCP to an X16D
   already running with `--dapport`. X16M doesn't own its lifetime: disconnecting closes the
   socket but leaves X16D running.
2. `--x16d <path-to-X16D.exe>` (or `X16D_PATH`) — spawn X16D as a child process.
3. a bundled copy at `x16d/X16D.exe` next to `X16M`'s own executable (see "Bundling" below).

For local development, build `BitMagic.X16Debugger/X16D` yourself (Debug is fine).
`Properties/launchSettings.json` has profiles for both modes:

- **"X16M (Debug/Release X16D)"** — spawns the sibling `X16D` build via `X16D_PATH`.
- **"X16M (attach to running X16D)"** — connects via TCP to `localhost:2563`. Start X16D
  yourself first with `--dapport 2563` (e.g. F5 the `X16D` project in another Visual Studio
  instance) to step through the debugger's own code while X16M drives it.

```
dotnet run --project BitMagic.X16MCP/X16M -- --x16d <path-to-X16D.exe>
# or
dotnet run --project BitMagic.X16MCP/X16M -- --x16d-host localhost --x16d-port 2563
```

Note: `X16D.exe` needs `EmulatorCore.dll` alongside it and a `rom.bin` reachable per
`BitMagic.X16Debugger`'s own build/run instructions (see `BitMagic.X16Emulator/X16E/fetch-rom.sh`
/ `fetch-rom.bat` for fetching a ROM).

## Bundling

X16M's own build does **not** copy X16D in — that would fight the F5 workflow above by forcing
a rebuild/copy every time you want to step into X16D. Producing a self-contained bundle (X16D +
its native DLLs + ROM copied into `x16d/` next to `X16M.exe`, matching resolution order item 3
above) is a CI concern, not something `dotnet build`/`dotnet publish` does locally.

## Tools (first slice — standard DAP only)

- `launch_project(projectPath)` — launches a project's `.json` (or a `.bmasm` file directly)
- `set_breakpoints(file, lines[])`
- `continue_execution()`, `step_over()`, `step_into()`, `step_out()`
- `get_stack_trace()`, `evaluate(expression)`, `disassemble(memoryReference, instructionCount)`,
  `read_memory(memoryReference, count)`
- `disconnect()`

X16-specific DAP requests (sprite/palette/layer/history/CPU profiler) aren't wired up yet — a
natural follow-up once this slice is proven out.

## Registering with an MCP client

For Claude Code, register X16M with the CLI rather than a project `.mcp.json` - the `--x16d`
path is machine-specific, and `BitMagic.X16MCP` isn't a repo of its own yet, so there's nowhere
sensible to check a project-scoped file in:

```
claude mcp add x16m --scope local -- <path-to-X16M.exe> --x16d <path-to-X16D.exe>
```

Other MCP clients typically want the equivalent of a `.mcp.json` entry:

```json
{
  "mcpServers": {
    "x16m": {
      "command": "<path-to-X16M.exe>",
      "args": ["--x16d", "<path-to-X16D.exe>"]
    }
  }
}
```

## Trying it out

`example/` has a minimal, self-contained program plus a walkthrough (launch, set a breakpoint,
step, evaluate an expression) - see `example/README.md`. It's a good first thing to run through
after registering X16M, and doubles as a smoke test after changing X16M itself.
