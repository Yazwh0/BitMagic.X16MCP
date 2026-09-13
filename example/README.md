# X16M example

A minimal program for trying out X16M without needing a real project. It counts from 0 to 10
in a loop, then halts:

```bmasm
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
`X16D.exe` (`Library/BM.bmasm`): no local copy needed. `project.json` gives the example its own
`binFolder` and sets `"rebuild": true`, which matters here: a bare `.bmasm` launch (no
`project.json`) defaults its compiled output to a `bin` folder next to `X16D.exe` itself, shared
by every other bare-file launch, so compiling two different bare `.bmasm` files in the same X16D
process can collide. A project-owned `binFolder` plus a forced rebuild avoids that entirely.

## Walkthrough

Once X16M is registered as an MCP server (see the top-level README), you don't call its tools
directly; you just ask your agent (e.g. Claude) in plain English, and it picks the right tools
for you. Point it at this folder's `project.json` and have a conversation like this:

> **You:** Launch the example project at `BitMagic.X16MCP/example/project.json` and put a
> breakpoint on the line that increments the counter.
>
> **Agent:** Launched it; the target's paused at the start. `inc counter` is on line 11 of
> `main.bmasm`, and I've set a breakpoint there.

> **You:** Continue, and tell me what `counter` is.
>
> **Agent:** Hit the breakpoint. `counter` is `0x01`.

> **You:** Keep continuing until the program finishes, telling me the counter each time.
>
> **Agent:** `0x02`... `0x03`... ... `0x0a`; then one more continue ran past the loop into
> `stp` and the target terminated. Final value was 10.

> **You:** What's the call stack right now?
>
> **Agent:** There's no active session; the target already terminated. Want me to launch it
> again?

> **You:** Yes, and disconnect once you've confirmed it's running.
>
> **Agent:** Launched and confirmed it's running, then disconnected the session.

Under the hood that's `launch_project` → `set_breakpoints` → a `continue_execution` /
`evaluate("counter")` pair repeated until the target reports terminated → `disconnect`. This
is also a handy smoke test after changing X16M itself: the same sequence is what proved the DAP
round-trip (breakpoints, stepping, expression evaluation) actually works end-to-end against a
real X16D, not just that the MCP tool schemas look right.
