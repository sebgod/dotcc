# Goose: its generated C through dotcc

[Goose](https://github.com/aardappel/goose) (Apache-2.0) is a heap-free systems language that
compiles a program, runtime included, to one standalone C file. Like Lua and chibi-scheme, that
C is a real-world corpus for dotcc, with one property the others lack: **a Goose program never
uses a heap**, so any managed allocation in its dotcc build is dotcc's own. Two goals, in order:

- **(a) Run it.** Every Goose sample compiles through dotcc and prints what Goose's own build
  prints. Each wall becomes a dotcc improvement, never a shim in the input.
- **(b) A heap-free .NET build.** Make "no managed allocation after startup" a property dotcc's
  output can be held to, with Goose's samples as the corpus, then aim the same output at a
  barebones NativeAOT target.

Runbook and CI: [examples/goose/README.md](../../examples/goose/README.md),
[`.github/workflows/goose.yml`](../../.github/workflows/goose.yml).

## (a) Run it: DONE

Pinned to Goose **v2026.4**. All 27 non-graphics samples are byte-identical to their reference
(`goose run` for 25 of them, gcc 15 for the 2 thread samples `goose run` cannot run), from the C
Goose writes with nothing edited and no header shimmed. The 5 graphics samples need a window
and are out of scope.

dotcc predefines neither `_WIN32` nor `__linux__`/`__GNUC__`, so Goose takes its generic POSIX
branch on every host, Windows included. What that took, each brick a general dotcc change:

| Brick | What was wrong | Now |
|---|---|---|
| `#pragma pack` | Silently ignored: Goose's packed records and tagged unions read fields at the wrong offsets (wrong output, no error). | `OnPragma` leaves a carrier token where the directive stood; `PragmaPackRewriter` (after macro expansion, so a macro's `struct` is covered) stamps the cap on each `struct`/`union` keyword; the layout model caps member and aggregate alignment and the backend emits `[StructLayout(..., Pack = N, Size = S)]`. `N`, `()`, `push`/`pop` with labels. `-E` prints the directive back. |
| Comma in a ternary arm | `i < n ? i : (fail(...), 0)`, Goose's bounds check, lowered to an immediately invoked lambda: CS1686 wherever a captured local's address was taken, and a heap closure per evaluation. | At a statement that can take temps, such a ternary becomes an if/else over a temp, each arm's comma operands statements of its own branch (`CondViaTemp`). |
| GNU `__attribute__((...))`, `__thread` | Parse errors: Goose's non-MSVC branch assumes GNU spellings. | `__attribute__` is a DeclSpec (promoted from `ID` after macro expansion, so a header's `#define __attribute__(x)` still wins), its list the C23 `AttrList` (now with argument lists, `format(printf, 1, 2)`); `noreturn` and `always_inline` lower, the rest are accepted. `__thread` promotes onto `_Thread_local`. |
| GCC `__atomic_*` builtins | Emitted as calls to nothing. | Lowered onto the `Atomic.*` helpers `<stdatomic.h>` uses; `__ATOMIC_*` predefined. |
| POSIX surface | Missing headers and calls. | `<sys/mman.h>` (libc on Linux/macOS; `VirtualAlloc` on Windows), `<pthread.h>` + `<sched.h>` (over `<threads.h>`'s tables), `sigaltstack`/`stack_t`, `_setjmp`/`_longjmp`/`sigsetjmp`, `flockfile`/`ftrylockfile`/`funlockfile` and the `*_unlocked` calls, `nanosleep`, `ssize_t` from `<unistd.h>`. |
| `snprintf(NULL, 0, ...)` | Wrote the NUL at `dst[0]`: a crash on NULL, a one-byte overrun otherwise. | Stores nothing, returns the length. |
| libc prototypes | `memcpy(void*, void*, int)`, `strlen(char*)`, ...: false "discards const" warnings on every const buffer. | The read-only operands of `<string.h>` and `write` are const. (The `int` counts stay: see deferred.md.) |

Pinned by the fixtures `gnu-attributes/`, `pragma-pack/`, `comma-ternary-temp/`, `posix-mman/`,
`pthreads/` (all gcc-matched) and the unit tests `GooseSurfaceTests`, `LibcGooseRuntimeTests`.

### Left from (a)

- ~~Reject calls to undeclared functions.~~ Done: an error from C99 on (gcc 14's wording), a
  warning under C90. Turning it on found the C99 `<math.h>` functions the runtime lacked:
  Goose's runtime calls `fma`/`fmaf` (in a helper no sample reaches, so it never reached the
  C# before), CPython's math modules `fma`, `log1p`, `nextafter` and `asinh`. All of C99 7.12
  but `erf`/`erfc`/`tgamma`/`lgamma` is in now, and `printf` prints infinities and NaNs as C
  does.
- ~~A comma in a short-circuit operand or a loop condition still lowers to the lambda.~~ Done:
  `&&`/`||` become an if over a bool temp, and such a loop evaluates its condition at the top
  of each iteration (`while (true) { ...; if (!cond) break; ... }`, a do-while through a label
  its `continue` reaches). Fixture `comma-short-circuit-loop/`.

## (b) Heap-free .NET: NOT STARTED

Measured on the 8 samples that passed first (2026-11-26, before the comma fix): 13 to 36 KB of
managed allocation per run and **no garbage collection at all**. The known per-call allocators
in dotcc's output:

- the comma lambdas (gone where a temp could take their place, above);
- `params VaArg[]` on each call to a variadic function (C# 13's `params ReadOnlySpan<VaArg>`
  would keep it on the stack);
- `SprintfBuilder`'s `StringWriter`, string and `byte[]` per `sprintf`.

Plan: a test that runs each Goose sample and asserts its allocation after startup is zero
(`GC.GetTotalAllocatedBytes` around `main`), then remove the allocators it finds; then a
NativeAOT publish with a trimmed runtime as the barebones target, and as a stretch a no-GC
runtime (bflat-style; not yet checked).
