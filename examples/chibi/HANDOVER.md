# chibi-scheme through dotcc — status & runbook

**Goal:** transpile the chibi-scheme core (the R7RS-small reference
implementation, plain portable C) to .NET via dotcc and pass its R7RS test
suite — the second real-world whole-program proof after Lua 5.5, and the
future hermetic oracle for a dotcc Scheme frontend (see
`~/.claude/plans/dotcc-scheme-frontend.md` context: dotcc transpiles its own
conformance reference, no external toolchain to pin).

## Status (2026-06-11)

**Phase 1 — parse/emit: DONE.** All 9 core TUs compile to `.cs` object
fragments: `gc sexp bignum gc_heap opcodes vm eval simplify main` (9/9 in
`probe.sh`; started 0/9).

**Phase 2 — link: DONE.** `link.sh` compiles all 9 TUs whole-program via
`--emit=build` → **0 Roslyn errors** (started at ~900 across ~10 root causes;
every fix upstreamed into dotcc, see "What it took", second list). And it
RUNS — phase 3 is most of the way there for free:

```bash
bash link.sh    # → dotcc: OK. dotnet build/bin/Release/net10.0/build.dll [args]
CHIBI_IGNORE_SYSTEM_PATH=1 CHIBI_MODULE_PATH=chibi-src/lib \
  dotnet build/bin/Release/net10.0/build.dll -q -p '(+ 1 2)'   # → 3
```

The full boot loads `init-7.scm` + `meta-7.scm` — module system, reader, VM,
GC, error backtraces all work — and stops exactly at `srfi/69/hash.so`: the
documented static-clibs milestone (phase 3 remainder), not a bug. Phase 4
(R7RS suite vs `baseline-r7rs.txt`) not yet attempted.

## Layout

| File | Role |
|---|---|
| `fetch.sh` | (Re)pin the vendored tree — clones `ashinn/chibi-scheme` at the pinned commit (`6fd23611`, master 2026-06, v0.12.0 "magnesium"), strips `.git`. **LF endings forced** (`core.autocrlf=false`): the Makefile splices `cat RELEASE` into a C string in generated `install.h`; a CR there breaks the reference build. |
| `chibi-src/` | The vendored flat snapshot. Pristine — never edit; builds happen in scratch copies. |
| `gen-include/chibi/install.h` | The Makefile-GENERATED config header, captured from the WSL reference build (probe needs it; the pristine tree doesn't carry it). Regenerate by re-running the reference build below. |
| `probe.sh` | The phase-1 scoreboard: `dotcc --emit=obj` per TU + first error. Re-run after each compiler fix. |
| `link.sh` | The phase-2 harness: all 9 TUs + probe.sh's FLAGS → `--emit=build` into `build/`. `link.sh file` emits a single `.cs` for eyeballing. |
| `baseline-r7rs.txt` | The oracle snapshot: full `tests/r7rs-tests.scm` output from the gcc-built chibi (timings normalized out). **1225/1225 tests, 18/18 subgroups.** |

## Reference build (the proven gcc-in-WSL oracle way)

```bash
# build in a scratch copy (the build generates install.h, .o, .so in-tree)
wsl -e bash -c 'cp -r /mnt/c/.../examples/chibi/chibi-src ~/chibi-ref && cd ~/chibi-ref && make -j8 all'
# smoke + suite
wsl -e bash -c 'cd ~/chibi-ref && LD_LIBRARY_PATH=. CHIBI_IGNORE_SYSTEM_PATH=1 CHIBI_MODULE_PATH=lib ./chibi-scheme tests/r7rs-tests.scm'
```

gcc 13.3 aarch64. `make all` also builds the dlopen'd C-stub modules
(`lib/srfi/69/hash.so` etc.) that the R7RS suite imports — core-only builds
fail at `(srfi 69)`.

## dotcc configuration (probe.sh `FLAGS`)

- `-I gen-include -I chibi-src/include`
- `-D SEXP_USE_INTTYPES -D SEXP_USE_NTPGETTIME` — mirrors the reference build.
- `-D SEXP_USE_DL=0` — **static modules**: no dlopen on .NET; chibi's own
  static builds use the same. Phase-3 consequence: the C-stub lib modules must
  be linked statically (chibi's `clibs.c` mechanism) or stubbed.
- `-D SEXP_USE_POLL_PORT=0` — declared but NOT effective (sexp.h
  unconditionally re-defines it to 1 on the non-`_WIN32` path); harmless.
  The poll path parses against dotcc's `<unistd.h>` select() surface, which
  throws at runtime if ever called (single-threaded runs never call it).

## What it took (all upstreamed into dotcc, all suites green)

Compiler gaps found and fixed — each was blocking ALL or most TUs:

1. **Subdirectory-qualified includes** — `#include "chibi/sexp.h"` vs `-I include` (`Compiler.BuildIncludeMaps` now recurses, keys by dir-relative path).
2. **Whitespace-indented directives** — `# ifdef` / `#  define` (lexer rules `#[ \t]*name`).
3. **LP64 predefines** — `__LP64__` / `__SIZEOF_POINTER__` / `__SIZEOF_LONG__`; without them chibi's `SEXP_64_BIT` config selects 32-bit pointer tagging → runtime miscompute, not a parse error.
4. **TypeNameRewriter alias-index bug** — fn-ptr typedef emission exempted the LAST body ID from TYPE_NAME promotion; with a typedef-name param (`typedef sexp (*sexp_proc1)(sexp, sexp, sexp_sint_t);`) that's the param, not the alias. Registration/emission now share one `FindAliasIndex`.
5. **Unary plus** — `+1`, `(+1 | (x >> 63))` (`sexp_fx_sign`); grammar + `SizeofFolder` follow-set.
6. **Array typedefs** — `typedef char sexp_abi_identifier_t[8];` → alias registered as `CType.Array`; member/param-decay/sizeof/local fall out of the typed IR.
7. **offsetof dotted member-designators** — `offsetof(struct sexp_struct, value.type.f)` (`sexp_offsetof`); `OffsetofPath` grammar + path-walking layout fold.
8. **Enum trailing comma** — `enum { …, SEXP_NUM_CPX, };` (the `#if`-composed enum-body idiom).
9. **Array declarator in multi-declarator tail** — `char *str=NULL, numbuf[NUMBUF_LEN];` → brace-less `Seq` of DeclStmt + ArrayDecl.
10. **`off_t`** (synthetic `<stdio.h>`) and a minimal **`<unistd.h>`** (usleep/isatty faithful; select surface compiles, throws at runtime).

Fixtures added: `unary-plus/`, `array-typedef/`, `offsetof-path/` (all
gcc-oracle-validated).

**Phase 2 (link) root causes, also all upstreamed** (each one a wall of
repeated Roslyn errors across the merged program):

1. **Whole-program internal-linkage merge** — header-defined `static inline`
   fns / `static const` globals re-arrive once per TU (CS0111/CS0102): IR-level
   dedup by structural fingerprint; same-name-different-body statics get
   per-TU uniquified names.
2. **goto label stacked on a case label** (`load_primitive: case 'Q':`) —
   label deferred and re-attached after the case labels.
3. **goto INTO a nested block** (CS0159; `goto adjust`, bignum.c) —
   `GotoScopeNormalizer` hoists the labeled tail out one level at a time
   (if-arms, else-if chains, plain blocks).
4. **Out-of-range constant casts** (CS0221 ×594) → `unchecked(...)`;
   **unary `-` on unsigned** → `unchecked(0UL - x)`.
5. **Integer promotions on unary `+ - ~`** (CS0266 ×66, `sign = -sign`) —
   `CType.IntegerPromote` at the IR (benefits the wat backend too).
6. **NULL into fn-ptr sinks** (CS0266 ×54) and **method-group casts in opcode
   tables** (CS8812/CS8757 ×230) — null rule + own-type pin in the backend.
7. **Compound-assign RHS conversions** (`long |= ulong`, `int += long`) — C#
   only narrows compound RHS when it implicitly converts; cast otherwise.
8. **Pointer-cast macros as case labels** (CS9135) — const-fold to the literal.
9. **C tag-vs-ordinary namespace collision** (`enum sexp_number_types` + the
   same-named global, CS0119) — `DotCcGlobals.`-qualify shadowed globals.
10. **`#include "opt/fcall.c"`** — `.c` files now `#include`-able (registered
    lazily by path so sibling `.c` files aren't eagerly slurped).
11. **POSIX surface** — `fcntl.h`, `poll.h`, `sys/{types,stat,time,select,socket}.h`,
    `fileno`/`close`/`read`/`write`, `stat`/`fstat`, `gettimeofday`,
    `strcasecmp`/`strncasecmp`, `fabsl`, ENOTSOCK `shutdown` (PosixLib.cs).

## Next milestones (the Lua playbook, phases 6+)

1. ~~**Link harness**~~ DONE — see Status.
2. ~~**Boot**~~ DONE (fell out of phase 2) — full init-7.scm/meta-7.scm boot,
   stops only at the dlopen'd srfi stub.
3. **Static clibs**: generate/compile `clibs.c` (chibi's static-module
   registry) so `(srfi 69)` etc. resolve without dlopen
   (`-D SEXP_USE_STATIC_LIBS`, chibi's `tools/chibi-genstatic`).
4. **R7RS suite**: `tests/r7rs-tests.scm` output `ShouldBe` `baseline-r7rs.txt`.
   Consider a `chibi.yml` workflow mirroring `lua.yml`.

**Why static, not dlopen (and the dlopen roadmap item).** `.so` loading is
dynamic, but chibi's static-clibs mechanism removes the dynamism while keeping
the `.so` *names*: under `SEXP_USE_DL=0`, `sexp_load_dl` is `#define`d to
`SEXP_UNDEF` and `sexp_load_binary` (eval.c) falls back to
`sexp_find_static_library` — the module path string becomes a key into a
compiled-in table (`clibs.c`, generated by `tools/chibi-genstatic`, which
`#define`-renames each stub's `sexp_init_library` and `#include`s the stub `.c`
directly — riding dotcc's lazy `.c`-include support). This is chibi's own
answer for no-dlopen platforms (emscripten), and a managed .NET host is one.
TRUE dynamic loading is a dotcc roadmap item, not a chibi blocker — see the
`dlfcn.h` row in `C-SUPPORT.md` (POSIX headers): `-shared` host exports exist
today; the missing piece is an import mode resolving a plugin TU's unresolved
externs as `[LibraryImport]` bindings against the host library.

## Semantic risk notes (for the runtime phases)

- chibi's GC is **precise with explicit root registration**
  (`sexp_gc_var/preserve` macros) — no conservative stack scanning, so the
  NativeMemory-backed heap should work. `gc.c` does arithmetic on object
  addresses (heap chunking) — fine over `malloc` pointers.
- **Tagged pointers**: fixnums tag the low bits of `sexp` (a pointer-width
  word). Requires real stable addresses (NativeMemory: yes) and the LP64
  predefines (above).
- `sexp_sizeof` uses `offsetof(struct sexp_struct, value) + sizeof(member)` —
  exercised by every allocation; the offsetof layout fold must agree with
  .NET's blittable layout (it does, by construction).
- The FAM over-allocation note from C-SUPPORT (`[1]`-modelled flexible
  members) applies to chibi's `sexp_struct` unions — harmless, but heap-size
  accounting in `gc.c` may see slightly larger objects than C did.
