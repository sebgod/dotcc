# chibi-scheme through dotcc — status & runbook

**Goal:** transpile the chibi-scheme core (the R7RS-small reference
implementation, plain portable C) to .NET via dotcc and pass its R7RS test
suite — the second real-world whole-program proof after Lua 5.5, and the
future hermetic oracle for a dotcc Scheme frontend (see
`~/.claude/plans/dotcc-scheme-frontend.md` context: dotcc transpiles its own
conformance reference, no external toolchain to pin).

## Status (2026-06-10)

**Phase 1 — parse/emit: DONE.** All 9 core TUs compile to `.cs` object
fragments: `gc sexp bignum gc_heap opcodes vm eval simplify main` (9/9 in
`probe.sh`; started 0/9). Phase 2 (Roslyn-compile the merged program), phase 3
(run `init-7.scm` / the REPL), phase 4 (R7RS suite vs `baseline-r7rs.txt`)
not yet attempted.

## Layout

| File | Role |
|---|---|
| `fetch.sh` | (Re)pin the vendored tree — clones `ashinn/chibi-scheme` at the pinned commit (`6fd23611`, master 2026-06, v0.12.0 "magnesium"), strips `.git`. **LF endings forced** (`core.autocrlf=false`): the Makefile splices `cat RELEASE` into a C string in generated `install.h`; a CR there breaks the reference build. |
| `chibi-src/` | The vendored flat snapshot. Pristine — never edit; builds happen in scratch copies. |
| `gen-include/chibi/install.h` | The Makefile-GENERATED config header, captured from the WSL reference build (probe needs it; the pristine tree doesn't carry it). Regenerate by re-running the reference build below. |
| `probe.sh` | The moving scoreboard: `dotcc --emit=obj` per TU + first error. Re-run after each compiler fix. |
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

## Next milestones (the Lua playbook, phases 6+)

1. **Link harness** (`link.sh` analog): all 9 TUs + `--emit=build`, chase
   Roslyn compile errors in the emitted C# (this is where cross-TU merge and
   emit-shape issues surface — expect a wall of them; Lua had its own).
2. **Boot**: `chibi-scheme -q` with `CHIBI_MODULE_PATH` pointing at
   `chibi-src/lib` — needs `main.c`'s option parsing, file I/O for
   `init-7.scm`, and the sexp reader/VM actually working.
3. **Static clibs**: generate/compile `clibs.c` (chibi's static-module
   registry) so `(srfi 69)` etc. resolve without dlopen.
4. **R7RS suite**: `tests/r7rs-tests.scm` output `ShouldBe` `baseline-r7rs.txt`.
   Consider a `chibi.yml` workflow mirroring `lua.yml` once phase 2 is green.

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
