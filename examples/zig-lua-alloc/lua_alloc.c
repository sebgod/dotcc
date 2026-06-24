// Milestone W, part 2 — a C `lua_Alloc`-shaped allocator, consumed by Zig as a real
// `std.mem.Allocator` (see main.zig).
//
// This is the exact Lua allocator contract: `nsize == 0` frees `ptr` and returns NULL;
// otherwise it (re)allocates to `nsize` bytes, preserving contents up to the smaller of the two
// sizes (that is just `realloc`). `ud` is opaque host userdata threaded straight through — here a
// `size_t` byte-counter the host reads back to prove the allocator was actually exercised.
#include <stdlib.h>
#include <stddef.h>  /* size_t */

void *lua_alloc(void *ud, void *ptr, size_t osize, size_t nsize) {
    (void)osize;
    if (nsize == 0) {
        free(ptr);
        return NULL;
    }
    if (ud != NULL) {
        *(size_t *)ud += nsize;
    }
    return realloc(ptr, nsize);
}
