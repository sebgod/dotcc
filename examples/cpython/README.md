# CPython through dotcc

The Python milestone compiles CPython 3.13 with dotcc (GH #174, #175). This
directory builds the interpreter from its C sources, runs Python on it, and
holds the probe that measures how much of the tree compiles, with the CI job
(`.github/workflows/python.yml`, GH #201) that keeps it from sliding back.

| File | What it is |
|---|---|
| `fetch.sh` | Shallow, sparse clone of the pinned tag (`v3.13.15`) into `cpython-src/` (git-ignored). CPython is too large to vendor the way `examples/lua/lua-src` is. |
| `generate.sh` | The sources CPython's own build generates: the frozen modules (`Programs/_freeze_module.py` under a host Python 3.13) and `Modules/config.c`, the built-in module table. |
| `build.sh` | Compiles the interpreter's `core` and `boot` units to objects (`dotcc --emit=obj`, into `build/obj/`), links them into one C# program and builds it with `dotnet`. |
| `run.sh` | Runs the built interpreter with the pinned tree's `Lib/` as its standard library. |
| `smoke.py` | A script over core language features and the importable pure-Python library; its stdout must equal `smoke.expected`, which host CPython 3.13 prints too. |
| `programs/`, `programs.sh` | Larger pure-Python programs (n-body, fannkuch, spectral-norm, a Richards-style scheduler, a Scheme interpreter, a language tour, the object model and GC, Unicode text and file I/O, tracebacks), each with the `.expected` output host CPython 3.13 prints. `programs.sh` runs them and diffs. |
| `files.txt` | The 202 translation units: `group\|path\|flags`. `core` is the interpreter (Parser, Objects, Python), `boot` the `Setup.bootstrap` modules, `extra` modules beyond those. `@TREE@` in a flag is the source tree. |
| `include/pyconfig.h` | dotcc's platform config: CPython's `./configure` output from Ubuntu x86_64, adjusted for dotcc (each change marked `dotcc:`). See its header. |
| `probe.sh` | Compiles each unit with `dotcc --emit=obj` and compares the set that emits with `emits.txt`. |
| `emits.txt` | The ratchet: the units that emit today. |

## Building and running the interpreter

```bash
dotnet build DotCC -c Release
examples/cpython/fetch.sh
PYTHON=python examples/cpython/generate.sh   # needs a host Python 3.13
examples/cpython/build.sh                    # about 5 minutes on 16 cores
examples/cpython/run.sh -c "print('hello')"
examples/cpython/run.sh examples/cpython/smoke.py | diff examples/cpython/smoke.expected -
HOST_PYTHON=python examples/cpython/programs.sh   # every program, timed against host CPython
```

`build.sh` recompiles a unit when its object is older than the source or than
the dotcc build (`dotcc.dll` or `DotCC.Lib.dll`), so after a compiler change it
redoes everything and after a source edit only that unit. `BUILD_JOBS` sets the
parallelism, and `BUILD_OBJ` the object directory: the probe's `out/cs` holds the
same objects, compiled with the same flags, so CI builds the interpreter from
those, and only this build's units are linked. The pyconfig is Linux's, so `sys.platform` is `linux` even on a
Windows host, and a POSIX CPython splits `PYTHONHOME` on `:`: `run.sh` passes
the home without its drive letter, and a script is best named by a relative
path.

## Probing the tree

```bash
examples/cpython/probe.sh            # exit 1 when the result and emits.txt differ
examples/cpython/probe.sh --update   # rewrite emits.txt from the result
```

`PROBE_JOBS` sets the parallelism (default: the CPU count), `DOTCC_DLL` the dotcc
build, `CPYTHON_SRC` the tree. Per-unit logs land in `out/cs/<unit>.log`, and
`out/summary.txt` lists each unit that does not emit with its first diagnostic.

## In CI

`python.yml` runs the probe, then builds the interpreter from the probe's objects
and runs `print('hello')`, `smoke.py` and `programs.sh` on it, on every push to a
PR, nightly and on demand.

The programs use no C module outside the build (so no `math`, `random`,
`struct`, `unicodedata`): they measure the interpreter core and the pure-Python
library. Each prints only what is the same on every platform (no timings, ids,
hash-ordered sets or host paths), so its `.expected` comes from a host CPython
3.13 run (`python programs/<name>.py > programs/<name>.expected`).

## The ratchet

A unit on `emits.txt` that stops emitting fails CI as a regression. A unit that
starts emitting also fails it, until `probe.sh --update` adds it, so the list
always says exactly what dotcc compiles. Commit the updated list with the change
that moved it.

The probe runs on pristine CPython sources. When dotcc gains a header or libc
function that `pyconfig.h` turns off, turn it back on there and re-run the probe.
