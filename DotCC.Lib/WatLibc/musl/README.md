# Vendored musl libm

The `<math.h>` functions of the wat target's libc are musl's, compiled by dotcc together
with the program, as emscripten compiles musl into every module. They compute
bit-identical results to the same sources built natively (checked against gcc in WSL over
12,000 values across 30 functions).

- **Source:** musl 1.2.4, `src/math/`, as shipped in the emscripten 3.1.56 SDK workload
  pack (`Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk`, `tools/emscripten/system/lib/libc/musl`).
- **License:** MIT, see [`COPYRIGHT`](COPYRIGHT). The table files (`*_data.c`, `*_data.h`)
  are Arm's optimized-routines, MIT as well (SPDX headers in each file).
- **Selection:** the closure of what dotcc's public `<math.h>` declares. `sqrt`, `fabs`,
  `floor`, `ceil`, `trunc` and `copysign` (and their `f` forms) are not here: the wat
  backend emits them as single wasm instructions.
- **Also:** `strtod`/`strtof`/`strtold` and `atof` (`src/stdlib/`), over `__floatscan`
  (`src/internal/floatscan.c`) reading a string through `shgetc.h`'s pseudo-FILE, with the
  `long double` helpers it calls (`copysignl`, `fmodl`, `scalbnl`, which are the `double`
  ones here). Checked the same way: bit-identical to a native build over 3000 round trips
  and the hard cases (subnormals, halfway points, hex floats, overflow, end pointers).
- **Edits:** none to the code. The table definitions are renamed after the object each
  defines (`exp_data.c` is `__exp_data.c`), and a file defining several functions is split,
  one per file (`shgetc.c` into `__shlim.c` and `__shgetc.c`, `strtod.c` into `strtod.c`,
  `strtof.c` and `strtold.c`, each with its own copy of the static `strtox`), because the
  library binds a unit by the name a program uses and leaves undefined.

What musl's build gets from its own headers, these get from `../include/`: `libm.h` and
`features.h` adapted from musl's `src/internal/` and `src/include/`, a `math.h` with
musl's `double_t`, `INFINITY` and classification macros, and a `stdio_impl.h` whose
`FILE` is musl's `struct _IO_FILE` cut to the fields the library reads so far (a
program's `FILE` stays opaque, so the library is free to define it). Those headers are the library's
own; a program includes dotcc's `DotCC.Lib/include/math.h`.

Adding a function: copy its file (and whatever it calls, transitively) from the same musl
tree, and declare it in `../include/math.h` if the public header does not.
