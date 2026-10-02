# Vendored dlmalloc

The wat target's `malloc`, `free`, `calloc`, `realloc` and the rest of the C heap are Doug Lea's
dlmalloc 2.8.6, compiled by dotcc with the program as the rest of the wat libc is, the allocator
emscripten builds into every module by default.

- **Source:** `system/lib/dlmalloc.c` of the emscripten 3.1.56 SDK workload pack
  (`Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk`), unchanged, below a prelude in `malloc.c`.
- **License:** public domain (CC0), as its header says.
- **Configuration** (the prelude): memory from the wat runtime's `sbrk`
  (`__builtin_dotcc_sbrk`), which only grows, so no trimming and no `mmap`; no `mallinfo` or
  statistics; a 64 KiB page and 16-byte alignment (dotcc's `size_t` and pointers are 8 bytes
  in memory); and a lock of its own (`USE_LOCKS 2`): a spin lock on a C11 `atomic_int`, which
  is a real atomic in a threaded module and a plain load and store in any other. dotcc has no
  `<pthread.h>`, so the library's include directory has an empty one for dlmalloc to include.
- **Names:** dlmalloc's classic `#define dlmalloc malloc` mapping, in the prelude, since
  emscripten's copy names the public functions through weak aliases under `__EMSCRIPTEN__`
  only. The file defines `malloc` and, as its `dotcc-libc: also defines` line lists,
  `free`, `calloc`, `realloc` and the other public functions, so the library binds it once
  for whichever of them a program uses first.
