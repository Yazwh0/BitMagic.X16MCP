# X16M

An MCP server that lets an AI agent drive a BitMagic X16 debug session (breakpoints, stepping,
stack/memory inspection) the same way VSCode does via `BitMagic.X16Debugger`.

X16M does not modify or depend on the debugger's internals. It speaks DAP to X16D either by
spawning it as a plain child process (stdin/stdout, exactly as VSCode does) or by connecting
over TCP to one already running with `--dapport` (so X16D can be started and debugged
separately, e.g. under the Visual Studio debugger), and re-exposes a subset of the DAP protocol
as MCP tools.

This is a submodule of the BitMagic superproject (not wired into `BitMagic.sln`, since X16D is
bundled rather than referenced, see "Bundling" below).

## Building

```bash
dotnet build BitMagic.X16MCP/X16M
```

## Running

X16M needs to know how to reach X16D. It resolves this in order:

1. `--x16d-host`/`--x16d-port` (or `X16D_HOST`/`X16D_PORT`): connect over TCP to an X16D
   already running with `--dapport`. X16M doesn't own its lifetime: disconnecting closes the
   socket but leaves X16D running.
2. `--x16d <path-to-X16D.exe>` (or `X16D_PATH`): spawn X16D as a child process.
3. a bundled copy at `x16d/X16D.exe` next to `X16M`'s own executable (see "Bundling" below).

For local development, build `BitMagic.X16Debugger/X16D` yourself (Debug is fine).
`Properties/launchSettings.json` has profiles for both modes:

- **"X16M (Debug/Release X16D)"**: spawns the sibling `X16D` build via `X16D_PATH`.
- **"X16M (attach to running X16D)"**: connects via TCP to `localhost:2563`. Start X16D
  yourself first with `--dapport 2563` (e.g. F5 the `X16D` project in another Visual Studio
  instance) to step through the debugger's own code while X16M drives it.

```bash
dotnet run --project BitMagic.X16MCP/X16M -- --x16d <path-to-X16D.exe>
# or
dotnet run --project BitMagic.X16MCP/X16M -- --x16d-host localhost --x16d-port 2563
```

Note: `X16D.exe` needs `EmulatorCore.dll` alongside it and a `rom.bin` reachable per
`BitMagic.X16Debugger`'s own build/run instructions (see `BitMagic.X16Emulator/X16E/fetch-rom.sh`
/ `fetch-rom.bat` for fetching a ROM).

## Bundling

X16M's own build does **not** copy X16D in. That would fight the F5 workflow above by forcing
a rebuild/copy every time you want to step into X16D. Producing a self-contained bundle (X16D
and its native DLLs copied into `x16d/` next to `X16M.exe`, matching resolution order item 3
above) is instead a job for the BitMagic superproject's own CI
(`.github/workflows/build-test.yml`), which already builds X16D for both Windows and Linux.

## Tools (first slice: standard DAP only)

- `launch_project(projectPath)`: launches a project's `.json` (or a `.bmasm` file directly)
- `set_breakpoints(file, lines[])`
- `continue_execution()`, `step_over()`, `step_into()`, `step_out()`
- `get_stack_trace()`, `evaluate(expression)`, `disassemble(memoryReference, instructionCount)`,
  `read_memory(memoryReference, count)`
- `disconnect()`

## Resources

- `bmasm-syntax` (`docs://bmasm-syntax`): a purpose-written `.bmasm`/Template Engine syntax
  reference - directives, types, labels, scope, expressions, embedded C#, the BM library, and a
  worked example - for an agent to read before writing or editing `.bmasm` code. See
  `X16M/Resources/BmasmSyntax.md`.

X16-specific DAP requests (sprite/palette/layer/history/CPU profiler) aren't wired up yet, a
natural follow-up once this slice is proven out.

## Registering with an MCP client

For Claude Code, register X16M with the CLI rather than a project `.mcp.json`: any `--x16d`
path is machine-specific, so a `.mcp.json` checked into a repo (shared with everyone who opens
it) is the wrong fit. Two scopes are relevant, and the difference matters:

- **`--scope user`**: available from every project, in every session, on this machine. This is
  the one to reach for unless you have a specific reason not to.
- **`--scope local`**: only takes effect in sessions launched from the *exact directory* you
  were standing in when you ran `claude mcp add`. A session started anywhere else, even a fresh
  restart, won't see it, and `claude mcp list` will still report it as healthy since that check
  isn't tied to any one session.

Either way the registration lives in your own `~/.claude.json`, never in the repo.

If you've downloaded a release archive (see "Bundling" above), X16D is already bundled inside
as `x16d/`, so no `--x16d` is needed at all. `claude mcp add` stores the command exactly as
given, and Claude Code doesn't necessarily launch it from the directory you were standing in
when you registered it, so the path must be absolute, not `.\X16M.exe` or `./X16M`. From inside
the extracted folder, expand it to an absolute path with the shell itself:

```bash
# Windows (PowerShell)
claude mcp add x16m --scope user -- "$PWD\X16M.exe"

# Linux
claude mcp add x16m --scope user -- "$(pwd)/X16M"
```

If you registered it before and `claude mcp list` shows a relative path, a "Conflicting scopes"
warning, or the tools aren't showing up despite a full restart, remove the bad entry (or entries,
if it's registered in more than one scope) and re-add it: `claude mcp remove x16m --scope
<scope>`, then one of the commands above.

If you built X16M yourself from source instead, point it at your own `X16D` build explicitly:

```bash
claude mcp add x16m --scope user -- <path-to-X16M.exe> --x16d <path-to-X16D.exe>
```

Other MCP clients typically want the equivalent of a `.mcp.json` entry:

```json
{
  "mcpServers": {
    "x16m": {
      "command": "<path-to-X16M.exe>",
      "args": []
    }
  }
}
```

## Trying it out

`example/` has a minimal, self-contained program plus a walkthrough (launch, set a breakpoint,
step, evaluate an expression): see `example/README.md`. It's a good first thing to run through
after registering X16M, and doubles as a smoke test after changing X16M itself.
