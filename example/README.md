# X16M example

A minimal program for trying out X16M without needing a real project. It counts from 0 to 10
in a loop, then halts:

```asm
import BM="bm.bmasm";

BM.X16Header();

.constvar byte counter $02

    lda #0
    sta counter

.loop:
    inc counter
    lda counter
    cmp #10
    bne loop

    stp
```

`BM.X16Header()` and the `bm.bmasm` import resolve to the shared helper library shipped next to
`X16D.exe` (`Library/BM.bmasm`) - no local copy needed. `project.json` gives the example its own
`binFolder` and sets `"rebuild": true`, which matters here: a bare `.bmasm` launch (no
`project.json`) defaults its compiled output to a `bin` folder next to `X16D.exe` itself, shared
by every other bare-file launch, so compiling two different bare `.bmasm` files in the same X16D
process can collide. A project-owned `binFolder` plus a forced rebuild avoids that entirely.

## Walkthrough

With X16M registered as an MCP client (see the top-level README), try:

1. **`launch_project`** with `projectPath` set to this folder's `project.json` (an absolute
   path). It compiles and runs `main.bmasm`, pausing at the first instruction.
2. **`set_breakpoints`** with `file` set to `main.bmasm` and `lines: [11]` (the `inc counter`
   line).
3. **`continue_execution`** - stops at the breakpoint.
4. **`evaluate`** with `expression: "counter"` - reads `0x01`.
5. Repeat `continue_execution` / `evaluate` a few more times and watch `counter` climb.
6. Eventually `continue_execution` runs past the loop's exit (`counter` reaches 10) into `stp`
   and reports the target terminated, since there's nothing left to stop at.
7. **`disconnect`** to end the session.

This is also a handy smoke test after changing X16M: the same sequence is what proved the
DAP round-trip (breakpoints, stepping, expression evaluation) actually works end-to-end against
a real X16D, not just that the MCP tool schemas look right.
