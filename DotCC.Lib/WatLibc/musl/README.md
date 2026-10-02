# Vendored musl: libm and the core of stdio

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
- **stdio** (`src/stdio/`): the `FILE` machinery (`__towrite`, `__toread`, `__uflow`,
  `__overflow`, `__fwritex`) and `fwrite`, `fread`, `fputs`, `fgets`, `ungetc`, `fseek`,
  `ftell`, `rewind`, `setvbuf`, `setbuf`, `fflush`, `fclose`, `fileno`, `feof`, `ferror`,
  `clearerr`, the standard streams, and the printf family's wrappers (`printf`, `fprintf`,
  `sprintf`, `snprintf`, `vprintf`, `vsprintf`) over dotcc's own `vfprintf`/`vsnprintf`.
  Edits, all to fit a library with no locks and no weak symbols:
  - `FLOCK`/`FUNLOCK` are empty (`../include/stdio_impl.h`) and `fputc`/`putc`/`fgetc`/
    `getc`/`getchar` are `putc_unlocked`/`getc_unlocked` without `putc.h`/`getc.h`'s locking.
  - What musl reaches through a weak alias it reaches directly (`fflush(NULL)` flushes
    `stdout` and `stderr`), and the `__stdio_exit_needed` hooks are gone; `fclose` does not
    unlist a locked file.
  - `__stdio_write`/`__stdio_read`/`__stdio_seek`/`__stdio_close` call WASI's `fd_write`/
    `fd_read`/`fd_seek`/`fd_close` as emscripten's do, with WASI's own 8-byte iovecs (dotcc's
    pointers are 8 bytes in memory) and WASI's errno mapped to C's (`__wasi_errno`).
  - `stdout` is unbuffered, like `stderr`: the backend expands a printf with a literal format
    inline, writing straight to fd 1, and a buffer would hold back what went through `stdout`.
  - Split one function per file as above (`fseek.c` into `__fseeko_unlocked.c`, `__fseeko.c`
    and `fseek.c`; `ftell.c` likewise; `fwrite.c` into `__fwritex.c` and `fwrite.c`), and the
    `__stdin_FILE`-style objects are `static`.
  `tmpfile` (in memory, after `fmemopen`), `vsnprintf`, `vfprintf` and the open-file list are
  dotcc's own, in `..`.
- **scanf** (`src/stdio/vfscanf.c`, `vsscanf.c`, `sscanf.c`, `fscanf.c`, `scanf.c`, `vscanf.c`,
  `src/internal/intscan.c` as `__intscan.c`) and the **strtol family** (`src/stdlib/strtol.c`,
  its `__intscan` path, split one function per file). Edits: no weak aliases; `vfscanf`
  includes `../include/mbstate.h` (the conversion state and `mbrtowc`, UTF-8 to dotcc's 16-bit
  `wchar_t`, which are dotcc's own) where it included `<wctype.h>`, from which it used nothing;
  the `va_list` forms are declared in `../include/scanf_impl.h`.

- **Streams over files** (`src/stdio/fopen.c`, `__fdopen.c` as `fdopen.c`, `__fmodeflags.c`)
  and `perror`. Edits: `fopen` opens through the libc's own POSIX `open` (`../open.c`, over
  WASI's `path_open`) and closes with `close`, not the syscalls; `fdopen` is defined under its
  own name (musl's `__fdopen` behind a weak alias), sets append mode with `fcntl` and line
  buffering with `isatty` (musl asks `ioctl(TIOCGWINSZ)`), and gives the stream no lock.
  `setvbuf` points exit's flush at `__stdio_exit` when it gives a stream a buffer (dotcc's
  `__ofl_lock` does too, for every stream on the open-file list): musl reaches its exit flush
  through weak symbols.
- **Strings**: `strdup`, `strndup`, `strnlen` (`src/string/`), unedited.
- **The environment** (`src/env/getenv.c`, `setenv.c`, `unsetenv.c`, `putenv.c`'s `__putenv`
  as `__putenv.c`, `setenv.c`'s `__env_rm_add` as `__env_rm_add.c`, `src/string/strchrnul.c`
  as `__strchrnul.c`). Edits: each includes `../include/env_impl.h`, which declares what musl's
  internal headers did and makes `__environ` `environ` (the libc's `../environ.c`, filled from
  WASI's `environ_get` by a constructor); the weak dummy `__env_rm_add`s are gone (the real one
  is linked); `__strchrnul` drops its word-at-a-time path (`__GNUC__` only).

What musl's build gets from its own headers, these get from `../include/`: `libm.h` and
`features.h` adapted from musl's `src/internal/` and `src/include/`, a `math.h` with
musl's `double_t`, `INFINITY` and classification macros, and a `stdio_impl.h` whose
`FILE` is musl's `struct _IO_FILE` (a program's `FILE` stays opaque, so the library is free
to define it). Those headers are the library's own; a program includes dotcc's
`DotCC.Lib/include/math.h` and `stdio.h`.

Adding a function: copy its file (and whatever it calls, transitively) from the same musl
tree, and declare it in `../include/math.h` if the public header does not.
