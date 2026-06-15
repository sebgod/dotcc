# ZIG-SUPPORT.md

What dotcc's **Zig front-end** supports today. The Zig axis is the second
implementer of the `IFrontend` seam (the C front-end is the first): a `.zig`
input is lexed + parsed by the LALR(1) grammar in [`DotCC.Lib/zig.lalr.yaml`](DotCC.Lib/zig.lalr.yaml)
(→ generated `DotCC.Zig`), lowered to the neutral typed IR by
[`ZigLowering`](DotCC.Lib/Frontends/ZigLowering.cs), and emitted by the **same**
C# backend + shell as C. So the lowered surface inherits dotcc's whole runtime
for free — including the libc routing that makes `printf` print.

This is an honest **subset dialect**, not full Zig: the grammar parses a
C-shaped value/type core, and lowering grows behind it deliberately ("fail
loudly, grow on purpose" — anything unlowered throws `IrUnsupportedException`
rather than miscompiling). `comptime`, generics, and `std` are out of scope by
design. Legend: ✅ supported (parses **and** lowers + runs) · 🚧 parses but does
not lower yet (loud error at the use site) · 🚫 not supported.

## Design intent — C interop without `@cImport`

Modern Zig **removed `@cImport`** (deprecated 0.16, gone in the 0.17-dev pin);
standalone-file C translation no longer exists (it's a build-system
`translate-c` step now). So dotcc's Zig C/libc interop uses the modern,
standalone-valid path: an **`extern fn` prototype + link libc (`-lc`)**. dotcc
marks such a prototype `FromSystemHeader` and renders the call **by its bare
name**, which routes it to dotcc's own `Libc` runtime — exactly how a C
program's libc call is handled. No `@cImport`, no header harvest.

## Declarations

| Feature | Status | Notes |
|---|---|---|
| `fn name(params) Ret { … }` | ✅ | top-level function definition |
| `pub fn …` | ✅ | `pub` unwrapped (visibility is a no-op in our single-module emit) |
| Parameters `name: Type` | ✅ | names + types ride into the C# signature; faithful signedness |
| Forward references | ✅ | two-pass lowering (Zig has no prototypes) — a call may precede the callee |
| `extern fn f(p: T) Ret;` | ✅ | libc/FFI prototype (no body); routed by bare name, linked with `-lc` |
| `extern fn f(p: T, ...) Ret;` | ✅ | **variadic** extern (e.g. `printf`); `...` must be last + extern-only |
| local `const`/`var` (typed or inferred) | ✅ | inside a function body |
| `fn f() !T` (inferred-error return) | 🚧 | parses; the `!` is dropped and `T` is used (error unions deferred) |
| top-level / global `const`/`var` | 🚫 | only function-local decls lower today |
| `export`/`inline`/`callconv`/`align`/`linksection` | 🚫 | full FnProto modifiers not modeled |
| `extern "c"` library-name string | 🚫 | bare `extern fn` only |

## Types

| Feature | Status | Notes |
|---|---|---|
| `i8 i16 i32 i64`, `u8 u16 u32 u64` | ✅ | faithful signedness (i8→`sbyte`, u8→`byte`, …) |
| `usize`/`isize` | ✅ | LP64 pointer-width (`ulong`/`long`) |
| `f32`/`f64` | ✅ | → C# `float`/`double` |
| `bool`, `void` | ✅ | |
| `c_char c_short c_ushort c_int c_uint c_long c_ulong c_longlong c_ulonglong` | ✅ | C-ABI types for `extern fn`, LP64-shaped |
| `*T`, `*const T` | ✅ | pointer (pointee `const` rides as a type qualifier) |
| `[*c]T`, `[*c]const T` | ✅ | C pointer (== C's `T*` / `const T*`) — printf's `[*c]const u8` format |
| `?T` optional | 🚧 | parses; does not lower |
| `[]T` slice | 🚧 | parses; the fat-struct lowering is not built |
| `[N]T` array | 🚧 | parses; does not lower |
| `E!T` error-union type | 🚧 | parses; treated as `T` |
| `comptime_int`/`comptime_float`, arbitrary `iN`/`uN` | 🚫 | |
| `[*]T` many-item, `[*:s]T` sentinel | 🚫 | only `[*c]` is tokenized |

## Statements

| Feature | Status | Notes |
|---|---|---|
| `if (c) … else …` | ✅ | condition wrapped in `Cond.B(…)` for C-truthy semantics |
| `while (c) …` | ✅ | (no payload / continue-expression yet) |
| `return e;` / `return;` | ✅ | |
| `x = e;` assignment | ✅ | |
| `_ = e;` discard | ✅ | Zig's mandatory discard of a non-void result |
| block `{ … }` | ✅ | |
| `for`, `switch`, `defer`/`errdefer`, labeled loops, `break`/`continue` | 🚫 | |

## Expressions

| Feature | Status | Notes |
|---|---|---|
| integer / float / string literals | ✅ | decimal int; string reuses C escape decoding (`\n \t \\ \" \xNN`) |
| identifiers, `(grouped)` | ✅ | |
| `or` `and` (short-circuit) | ✅ | |
| comparison `== != < > <= >=` | ✅ | non-associative (`a < b < c` is a parse error, like Zig) |
| bitwise `& ^ \|`, shift `<< >>` | ✅ | |
| arithmetic `+ - * / %` | ✅ | usual-arithmetic result typing (fixes i64 truncation) |
| prefix `-` `~` `!` | ✅ | |
| `if (c) a else b` (if-**expression**) | ✅ | → C# ternary |
| function call `f(args)` | ✅ | intra-Zig + forward-ref + libc-by-bare-name (incl. variadic `printf`) |
| prefix `&` (address-of), `try` | 🚧 | parse only (`try` needs error unions) |
| postfix `.field` `.*` `.?` `[i]` | 🚧 | parse only (no struct/array/optional lowering yet) |
| `@builtin(...)` (e.g. `@as`, `@intCast`) | 🚧 | parse only |
| `.enumLiteral` | 🚧 | parse only |
| wrapping/saturating ops, `orelse`/`catch` | 🚫 | |

## Lexer

| Feature | Status |
|---|---|
| decimal integers, floats, `"…"` strings, `//` line comments, `@name` builtins | ✅ |
| hex/oct/bin/underscored integers, char literals `'x'`, multiline `\\` strings, `\u{…}` escapes, escaped-quote `\"` in a string | 🚫 |

## Out of scope (the dialect line)

`comptime` (beyond const folding), generics / `anytype`, `@import("std")`,
container decls (`struct`/`enum`/`union`/`opaque`), error sets, anonymous init
lists `.{…}`, `async`/`suspend`, inline assembly, destructuring assignment.

## Known limits

- **`void` main** is unsupported: the shell wires `return main();`, so `main`
  must return an integer (`pub fn main() u8 { … return 0; }`). A `main(); return 0;`
  follow-up for `void` main is planned.
- **Mixed `.c` + `.zig`** translation units in one invocation are not wired yet.

## Validation

- **Always-on emit tests** — `DotCC.Tests/ZigFrontendTests.cs` pins dotcc's
  lowered C# for the supported surface (parameters, signedness, if/while, the
  if-expression, calls + forward refs, `extern fn` libc calls, variadic
  `printf`).
- **Differential oracle (opt-in)** — `DotCC.FunctionalTests/ZigOracleTests.cs`
  compiles + runs each program through dotcc **and** the real `zig` compiler and
  asserts they agree on exit code **and** stdout (`DOTCC_RUN_ZIG_ORACLE=1`; skips
  when no `zig` is on PATH). CI runs it on linux-x64 + windows-x64 against a
  pinned `zig 0.16.0`.
- **Examples** — `examples/zig-hello`, `examples/zig-extern` (putchar),
  `examples/zig-printf` (variadic printf).
