# CPython through dotcc

The Python milestone compiles CPython 3.13 with dotcc (GH #174, #175). This
directory is the probe that measures how far that has got, and the CI job
(`.github/workflows/python.yml`, GH #201) that keeps it from sliding back.

| File | What it is |
|---|---|
| `fetch.sh` | Shallow, sparse clone of the pinned tag (`v3.13.15`) into `cpython-src/` (git-ignored). CPython is too large to vendor the way `examples/lua/lua-src` is. |
| `files.txt` | The 202 translation units: `group\|path\|flags`. `core` is the interpreter (Parser, Objects, Python), `boot` the `Setup.bootstrap` modules, `extra` modules beyond those. `@TREE@` in a flag is the source tree. |
| `include/pyconfig.h` | dotcc's platform config: CPython's `./configure` output from Ubuntu x86_64, adjusted for dotcc (each change marked `dotcc:`). See its header. |
| `probe.sh` | Compiles each unit with `dotcc --emit=obj` and compares the set that emits with `emits.txt`. |
| `emits.txt` | The ratchet: the units that emit today. |

## Running it

```bash
dotnet build DotCC -c Release
examples/cpython/fetch.sh
examples/cpython/probe.sh            # exit 1 when the result and emits.txt differ
examples/cpython/probe.sh --update   # rewrite emits.txt from the result
```

`PROBE_JOBS` sets the parallelism (default: the CPU count), `DOTCC_DLL` the dotcc
build, `CPYTHON_SRC` the tree. Per-unit logs land in `out/cs/<unit>.log`, and
`out/summary.txt` lists each unit that does not emit with its first diagnostic.

## The ratchet

A unit on `emits.txt` that stops emitting fails CI as a regression. A unit that
starts emitting also fails it, until `probe.sh --update` adds it, so the list
always says exactly what dotcc compiles. Commit the updated list with the change
that moved it.

The probe runs on pristine CPython sources. When dotcc gains a header or libc
function that `pyconfig.h` turns off, turn it back on there and re-run the probe.
