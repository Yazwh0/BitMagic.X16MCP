# Debugging programs from other compilers

BitMagic is the primary language, but the debugger can also debug programs built by other compilers. Their symbols
are read into the same symbol tables as BitMagic's, so `evaluate`, `get_variables`, breakpoints and stepping all work
the same way, **using BitMagic's naming rules** rather than the other compiler's own syntax.

Each section below explains how a compiler's symbols map onto BitMagic names.

## ca65

Assembly programs built with `ca65` and `ld65` from the cc65 suite (C programs from the cc65 compiler aren't supported).
Symbols come from the debug file written by ld65 (`--dbgfile`), named by the project's `cc65` entry. The source must
be assembled with `-g` for line and scope information.

### Names

| ca65 | BitMagic name to use |
| ---- | -------------------- |
| Module level symbol, `counter` | `counter` or `Main:counter` |
| Symbol in a `.proc` / `.scope`, `sound::tick::row` | `sound:tick:row` or `Main:sound:tick:row` |
| Same, without the scope | `:row` - works from anywhere when the name is unique, otherwise the error lists the matches |
| Cheap local, `@loop` | Can't be evaluated |

Scopes are separated by a single `:`, not ca65's `::`. In BitMagic `::` means something else, so
`sound::tick::row` won't be found.

### Types

ca65 symbols have no type, so variables are typed by their size:

| Size | Shown as |
| ---- | -------- |
| 1 byte | `byte` |
| 2 bytes | `ushort` (word) |
| More | byte array |

The size comes from `.res`, or the data on the label's line. A table written over several lines runs up to the next
label. Read further array elements, or a word table, with `peek(address + n)` or `read_memory`, using the address
shown by `get_variables`.

### What is a variable

- **Variables** (listed by `get_variables`): labels in writable segments (BSS, DATA, zero page, banked RAM), zero page
  `equ`s such as `row_count = $ca`, and labels on data (`.byte`, `.word`) such as tables.
- **Not variables**: code labels and constants. They aren't listed, but `evaluate` returns their address or value,
  eg `evaluate load_module` gives the routine's address.

### Locals and Globals

- **Locals** shows the variables of the current `.proc` and its enclosing scopes. Module level variables are only
  shown in Globals.
- **Globals** nests variables by scope. Where a scope has a lot of variables (eg a memory map include file), they are
  grouped by segment, eg `ZEROPAGE`, `BSS`, `BANKMISC`. The group is for display only and isn't part of the name.

### Banked RAM and loaded files

- Variables in banked RAM (`$A000-$BFFF`) read whichever bank is currently selected.
- Files loaded at runtime (eg overlay modules in `DAT/`) are mapped back to source when the program loads them, so
  breakpoints in them only resolve once they're loaded.
