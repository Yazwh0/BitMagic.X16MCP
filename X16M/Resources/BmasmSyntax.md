# bmasm syntax reference

`.bmasm` is 65c02 assembly for the Commander X16, preprocessed by BitMagic's **Template
Engine**: embedded C# that runs at build time and writes assembly before the compiler ever sees
it. A `.bmasm` file is a mix of three things, line by line:

- **An opcode line** — a valid 6502/65c02 mnemonic, e.g. `lda #$00`, `bra loop`.
- **A directive or label** — starts with `.`. `.segment`, `.proc`, `.const` etc. are directives;
  a line that's just `.name:` (nothing else on it but whitespace before the `.`) is a label.
- **A comment** — starts with `;`, or `//` on a directive line.

**Any other line is plain C#** — `for`, `if`, `var x = ...;`, method calls, braces. No macro
language: this is how BitMagic replaces macros entirely.

```text
import BM="bm.bmasm";

BM.X16Header("entry");

.constvar byte counter $02
.proc entry
    sei
    jsr counting_test
    brk
.endproc

.proc counting_test
    lda #0
    sta counter
.loop:
    inc counter
    lda counter
    cmp #10
    bne loop
    rts
.endproc
```

## Directives

Parameters are positional (in the order below) or `name=value`; `_` skips a positional one.

| Directive | Parameters | What it does |
| --- | --- | --- |
| `.segment name [address] [maxsize] [filename] [scope]` | | Opens/switches to a named memory region. Re-naming an existing segment switches to it. |
| `.endsegment` | | Back to the `Main` segment/scope. |
| `.scope [name]` | | Opens a named constant scope (flat, doesn't nest; omit `name` for anonymous). |
| `.endscope` | | Back to the enclosing procedure's scope. |
| `.proc [name]` | | Named block with its own scope + an entry-point constant (`jmp myproc` works). Nestable. |
| `.endproc` | | Closes the proc; adds `endproc` pointing just past its code. |
| `.const name value` | | Named constant. |
| `.var type name [value]` | | Typed data: reserves space **and writes** the initial value (default `0`). |
| `.constvar type name [value]` | | Typed alias for a fixed address (hardware register, zero-page slot). Writes nothing. |
| `.padvar type name` | | Reserves space, writes nothing — for BSS/uninitialised RAM only. |
| `.org address` | | Advance the write position to `address`. Errors if already past it. |
| `.pad size` | | Advance the write position by `size` bytes. |
| `.align boundary` | | Advance until the write position is a multiple of `boundary`. |
| `.byte v, v, ...` | | Emit raw bytes. |
| `.word v, v, ...` | | Emit 16-bit words, little-endian. |
| `.code v, v, ...` | | Same bytes as `.byte`, but the debugger disassembles them as instructions. Use for hand-encoded/generated opcodes. |
| `.breakpoint` | | Breakpoint on the next line. |
| `.stop` / `.nostop enabled` | | Debugger stepping hints — `.nostop true` skips landing on lines until `.nostop false`; `.stop` punches a hole in that. |
| `.exception` | | Raise a debugger exception here (flag an error path). |
| `.debugload filename address` | | Tell the debugger a file's symbols now live at `address` (for a custom loader the debugger can't see). |
| `.debugalias type name value` | | Add a debugger-visible name for an address or register, emits nothing. |

### Types

`byte`/`sbyte` (1), `short`/`ushort` (2), `int`/`uint` (4), `long`/`ulong` (8), `string` (*n*),
`proc` (2, a procedure address), `ptr` (2, bare address with no target type). Only used for
size + debugger display — the compiler never type-checks. Arrays: `byte[16]`. Pointers: `ptr`
after the type, e.g. `ushort ptr`. Combined, bracket first: `byte[16] ptr` is 16 pointers (32
bytes), **not** a pointer to a 16-byte block.

## Labels

```text
.loop:
    inc counter
    bne loop
```

A label is `.name:` alone on its line (whitespace before the `.` only); anything else on that
line, including an instruction, is silently dropped. Belongs to the scope it's defined in.
Forward references are fine.

- **Repeated names** (same label defined more than once, for flow control) can't be used bare —
  prefix `-name`/`+name` for the nearest one before/after this line; repeat the prefix to go
  further (`--name` is the second match back).
- **Anonymous labels**: `.:` with no name. Reference with a bare `-`/`+` (same repeat rules).
  Unlike a named label, `.:` *can* share its line with an instruction, e.g. `.:	jmp -`.
- `loop-1` is a valid expression: the address just before `loop`.

## Scope and name resolution

Names (labels, constants, variables) live in a tree: `App` (root, machine constants like VERA
register names) → **scopes** (flat, don't nest, default `Main`) → **procedures** (nest freely).
A fully qualified name is joined with `:`, e.g. `App:Main:counting_test:counter`.

Resolution searches outward from where you are (current proc → its scope → `App`), so you
usually just write the bare name. Qualify only when ambiguous: `sound:init`,
`App::counter` (`::` is a wildcard for a skipped middle segment — error if more than one match).
This is also exactly the syntax the debugger's `evaluate` accepts, e.g.
`Main:MyProc:NestedProc:someVar`.

## Expressions

Anywhere a directive or opcode takes a value, an expression works instead of a literal (parsed
by CodingSeb.ExpressionEvaluator + a 65c02 extension). **Result must be a whole number** — use
the Template Engine (real C#) for anything fractional.

- Arithmetic: `+ - * / %` (`/` is integer division: `7/2` is `3`)
- Bitwise: `& | ~ << >>`
- Comparison/logic: `== != <= >= && || !` (each gives `1`/`0`)
- Ternary: `?:`
- Char literals: `'A'` is `65`
- Labels/constants resolve through scope, same as anywhere else

Number formats: `$9f20` / `0x9f20` (hex), `%1010_0000` / `0b10100000` (binary), `40736` (plain
decimal). `_` is a digit separator anywhere.

**Byte operators** `<` `>` `^` are redefined as *prefixes* (nothing to their left) that pull one
byte out of a value — the standard way to split an address for a `lda #<label` / `lda #>label`
pair:

| Prefix | Returns | `<$123456` |
| --- | --- | --- |
| `<` | low byte, `value & $ff` | `$56` |
| `>` | high byte, `(value & $ff00) >> 8` | `$34` |
| `^` | top byte, `(value & $ff0000) >> 16` | `$12` |

Binds tightly: `<target + 1` is `(<target) + 1`, not `<(target + 1)`. Because of this, use `<=`
`>=` `==` `!=` for comparisons, never bare `<`/`>` as comparison operators.

## The Template Engine (embedded C#)

Any line that isn't an opcode/directive/label/comment is C#, run at build time.

- `@(expr)` inside an assembly line splices in a C# expression's value:
  `ldx #@(hello_world_string.Length)`
- A line that's just `@expr` emits whatever `expr` evaluates to.
- To *emit* assembly from C#, call a method that writes it as a plain statement (no `@`) — e.g.
  `BM.Bytes(...)` below.
- `for`, `if`, `var`, method calls, LINQ all work. `System`, `System.Linq`,
  `System.Collections[.Generic]`, `System.Threading.Tasks` are pre-imported.

```text
for (var page = 0; page < 4; page++)
{
    lda #@(page * 64)
    sta some_table, x
}
```

### Header directives (top of file, before any opcode/`.`/C# line)

| Form | Meaning |
| --- | --- |
| `library Namespace.Class;` | Marks the file as a library (methods only, no body) that others `import`. |
| `import Name = "file";` | Builds `file` as a template and exposes it as `Name.Method(...)`. |
| `include "file.cs";` | Adds a plain C# source file, compiled alongside the template (no `Name.`/`@`). |
| `using Some.Namespace;` | C# `using`, header-only. |
| `reference Some.Assembly;` | Adds a .NET assembly by name (already resolvable). |
| `assembly "path/to/lib.dll";` | Adds an assembly from a file. |
| `nuget "Package.Id", "1.2.3";` | Downloads and adds a NuGet package's assemblies. |
| `!directive` | Runs `.directive` in the setup phase, before the body — e.g. `!segment Foo $a000` to define layout up front. |

### The BM library (`import BM="bm.bmasm";`)

```text
BM.Bytes(values, width = 16)         // .byte lines from int/sbyte/byte values, $XX each
BM.Bytes(text)                       // one byte per char, no encoding change
BM.Words(values, width = 16)         // .word lines, little-endian, from ushort/short
BM.HighBytes(values, width = 16)     // high byte of each value as .byte (pair with LowBytes
BM.LowBytes(values, width = 16)      //   for a lda lo,x / lda hi,x split lookup table)
BM.Petscii(text, addNullTermination = true)      // PETSCII bytes, no charset translation
BM.IsoPetscii(text, addNullTermination = true)   // as above, $40-$5f shifted down by $40
BM.StringToPetscii(text, addNullTermination = true)  // returns IEnumerable<byte> instead
BM.X16Header()                       // "10 SYS 2061" BASIC stub - code must start at $080d
BM.X16Header(label, invalidHeader = false)  // "10 SYS <label>" - label must resolve below 10000
```

## Worked example (real file, `BitMagic.Examples/HelloWorld`)

Shows self-modifying code with an inline operand label, an unrolled loop built from C#, and
`PRIMM` (kernal routine that prints a string immediately following the `jsr`):

```text
import BM="bm.bmasm";

const string hello_world_string = "HELLO WORLD!\r\n";

BM.X16Header();

.proc main
    nop
    jsr bsout_indexed
    jsr bsout_unrolled
    jsr primm
.:	jmp -
.endproc

; Indexed output - the standard way.
.proc bsout_indexed
    ldx #0
.loop:
    lda hello_world, x
    beq done
    jsr BSOUT
    inx
    jmp loop
.done:
    rts
.endproc

; Self-modifying: read_address: names the operand bytes of the lda below, so code
; elsewhere can overwrite them directly (`sta read_address` / `sta read_address + 1`).
.proc bsout_selfmodifying
    lda #<hello_world
    sta read_address
    lda #>hello_world
    sta read_address + 1
.:	lda read_address: $1234
    beq +
    jsr BSOUT
    inc read_address
    bne -
    inc read_address + 1
    jmp -
.:	rts
.endproc

; C# unrolls the loop entirely at build time - no runtime loop at all.
.proc bsout_unrolled
    var toDisplay = BM.StringToPetscii(hello_world_string).ToArray();
    for (var i = 0; i < toDisplay.Length; i++)
    {
        lda #@(toDisplay[i])
        jsr BSOUT
    }
    rts
.endproc

; PRIMM: the string bytes sit inline right after the jsr and execution resumes past them.
.proc primm
    lda ROM_BANK
    pha
    stz ROM_BANK
    jsr PRIMM
    BM.Bytes(BM.StringToPetscii(hello_world_string));
    pla
    sta ROM_BANK
    rts
.endproc

.hello_world:
    BM.Bytes(BM.StringToPetscii(hello_world_string));
```

## X16 hardware itself (not a bmasm concern)

This reference is the `.bmasm`/Template Engine *language*. For the X16's own memory map, VERA
registers, KERNAL routine addresses and I/O layout, that's not BitMagic-specific - go to the
[X16Community docs](https://github.com/X16Community/x16-docs), the
[X16 forums](https://cx16forum.com/forum/), or the project's Discord. `App`-level constant names
(register names etc.) that BitMagic exposes are generated from those same sources.
