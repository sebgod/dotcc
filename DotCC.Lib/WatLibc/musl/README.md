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
- **Edits:** none to the `.c` and `.h` files. The table definitions are renamed after the
  object each defines (`exp_data.c` is `__exp_data.c`), because the library binds a unit by
  the name a program uses and leaves undefined.

What musl's build gets from its own headers, these get from `../include/`: `libm.h` and
`features.h` adapted from musl's `src/internal/` and `src/include/`, and a `math.h` with
musl's `double_t`, `INFINITY` and classification macros. Those headers are the library's
own; a program includes dotcc's `DotCC.Lib/include/math.h`.

Adding a function: copy its file (and whatever it calls, transitively) from the same musl
tree, and declare it in `../include/math.h` if the public header does not.
