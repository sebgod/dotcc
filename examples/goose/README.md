# Goose through dotcc

[Goose](https://github.com/aardappel/goose) is a heap-free systems language: a program's data
lives on data stacks (address space reserved up front, committed as it is touched), values are
packed structs and tagged unions, and the compiler writes the whole program, runtime included,
as **one standalone C file**. That C is what dotcc compiles here, the same way it compiles Lua
and chibi-scheme: every wall it hit became a dotcc improvement, not a shim (the list is in
[docs/plans/goose.md](../../docs/plans/goose.md)).

## Run it

```bash
dotnet build DotCC -c Release
bash examples/goose/fetch.sh        # the pinned release (v2026.4), sha256-checked, into build/
bash examples/goose/run.sh          # every sample; JOBS=4 runs four at a time
bash examples/goose/run.sh 05_shapes 23_mandelbrot_threads
```

For each sample `run.sh` has Goose write its C (`goose -o x.c --standalone`), builds that C with
`dotcc --emit=build`, runs it, and diffs the output against a reference built without dotcc:

- `goose run`, which compiles the same C in process with TinyCC, for the console samples and
  `24_call_c` (built with `--include call_c.h`, the C header its `extern fn`s bind);
- gcc for `23_mandelbrot_threads` and `32_pool_worker_snapshots`, since `goose run` has no
  threads (TinyCC cannot place thread-local storage in memory). Without gcc on `PATH` (Windows)
  they are skipped.

The graphics samples (27 to 31) need a window and are left out.

## Status (2026-11-26)

All 27 non-graphics samples are byte-identical to their reference: the 24 console samples, both
thread samples and `24_call_c`. dotcc defines neither `_WIN32` nor `__linux__`, so Goose's C
takes its generic POSIX branch everywhere, Windows included: `mmap` data stacks with a
`PROT_NONE` guard gap, pthreads, `sigaltstack`, `_setjmp`.

On Windows an accessible `mmap` is committed when it is made (Windows has no overcommit), so
each data stack Goose reserves (2 GiB by default) counts against the commit limit; Goose halves
a region the platform refuses, and `goose -o --stack-reserve` lowers the reservation.
