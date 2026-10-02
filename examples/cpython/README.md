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
| `probe-wat.sh`, `emits-wat.txt` | The same probe for the wat back end: each unit through `dotcc --target=wat --emit=obj` and `wat2wasm`, with its own ratchet. |
| `build-wat.sh` | The interpreter as WebAssembly: writes a compilation database of the `core` and `boot` units and compiles them as one program with `dotcc --target=wat --compile-commands`, then assembles `build-wat/python.wasm`. |

## Building and running the interpreter

```bash
dotnet build DotCC -c Release
examples/cpython/fetch.sh
PYTHON=python examples/cpython/generate.sh   # needs a host Python 3.13
examples/cpython/build.sh                    # about 5 minutes on 16 cores (BUILD_AOT=1: also NativeAOT)
examples/cpython/run.sh -c "print('hello')"
examples/cpython/run.sh examples/cpython/smoke.py | diff examples/cpython/smoke.expected -
HOST_PYTHON=python examples/cpython/programs.sh   # every program, timed against host CPython
```

The default build is a JIT assembly, which compiles the interpreter's startup
path on every run (about 2 s before the first line of Python runs).
`BUILD_AOT=1` also publishes a NativeAOT interpreter (`build/python-aot`, about
two minutes more), which `run.sh` prefers when it is newer: it starts in under
0.1 s, like host CPython. Both carry dotcc's `longPathAware` manifest, so on
Windows they handle paths past 260 characters, a deep current directory
included.

`build.sh` recompiles a unit when its object is older than the source or than
the dotcc build (`dotcc.dll` or `DotCC.Lib.dll`), so after a compiler change it
redoes everything and after a source edit only that unit. `BUILD_JOBS` sets the
parallelism, and `BUILD_OBJ` the object directory: the probe's `out/cs` holds the
same objects, compiled with the same flags, so CI builds the interpreter from
those, and only this build's units are linked. The pyconfig is Linux's, so `sys.platform` is `linux` even on a
Windows host, and `os.path` is `posixpath`. The interpreter is therefore linked
with `-fposix-paths`: on Windows the runtime shows it POSIX paths (`/c/Users/...`
for `C:\Users\...`, `//server/share` for UNC) in `getcwd`, `sys.executable`,
`PATH` and the other path-valued environment variables, and accepts both forms
wherever a path goes in, so a script can be named by a Windows path too
(GH #254).

### The shared build: libpython and extension modules

```bash
BUILD_SHARED=1 examples/cpython/build.sh
PY_SHARED=1 examples/cpython/run.sh -c "import _heapq; print(_heapq.__file__)"
```

`BUILD_SHARED=1` also builds the interpreter the way CPython's shared build is
laid out, into `build/shared`. libpython is a managed library
(`python3.13/`, `dotcc -shared -fassembly`), the interpreter a thin program
linked against it (`python/`, only `Programs/python.c`), and each module in
`SHARED_MODULES` an extension module of its own: an assembly linked against
libpython, built with `Py_BUILD_CORE_MODULE` as CPython builds a shared stdlib
module, and copied to `lib-dynload/<module>.cpython-313-x86_64-linux-gnu.so`.
`PY_SHARED=1 run.sh` puts that directory on `PYTHONPATH`, so an import finds the
file and CPython's `dynload_shlib.c` loads it with `dlopen`. dotcc's runtime
loads a .NET assembly into its own load context, where the extension's reference
to libpython binds to the interpreter's copy: one runtime, heap and set of
globals for both. The default module is `_heapq`, which the static interpreter
does not carry (there `import _heapq` fails, and `heapq` falls back to Python).
A NativeAOT program cannot load an assembly, so the NativeAOT interpreter keeps
to the static link.

### The interpreter as WebAssembly

```bash
WAT2WASM=path/to/wat2wasm examples/cpython/build-wat.sh   # about a minute; build-wat/python.wasm, 4.4 MB
node --liftoff-only --stack-size=8000 Scripts/wat-run.js \
  --dir "$PWD/examples/cpython/build/home::/py" --env PYTHONHOME=/py \
  examples/cpython/build-wat/python.wasm -c "print('hello')"
```

`build-wat.sh` compiles the interpreter with dotcc's wat back end into one module, a WASI
command: the wat libc (musl's stdio and libm, dlmalloc, and a POSIX layer over WASI's
preopened directories) is compiled with it, and its static data (CPython's `_PyRuntime`,
the type objects, the keyword tables) is laid out as data segments at compile time. Under
node, `Scripts/wat-run.js` runs it: `--dir` preopens the home that `run.sh` sets up
(`build/home/lib/python3.13` is the tree's `Lib/`) as `/py`, and `PYTHONHOME` points CPython
at it. `smoke.py` and every program in `programs/` print what host CPython 3.13 prints (a
program that writes files in its current directory, or checks `sys.executable`, needs a
preopened `/`). It starts in about half a second on V8's baseline compiler; `--liftoff-only`
keeps node from waiting at exit for the optimizing compiler, which takes seconds over the
eval loop's dispatch (a browser runs that in the background). This is the interpreter the
browser sandbox runs (GH #269).

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
