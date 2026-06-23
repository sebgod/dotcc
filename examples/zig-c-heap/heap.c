/* C helpers for the shared-heap demo. Compiled in the SAME dotcc invocation as the
   .zig unit; both lower into one program over one heap. malloc/free here and Zig's
   std.heap.c_allocator are the SAME allocator, so memory crosses the seam freely. */
#include <stdlib.h>

/* malloc a buffer C-side; Zig will read it and free it through c_allocator. */
int *make_ints(int n) {
    int *p = malloc(n * sizeof(int));
    for (int i = 0; i < n; i++) {
        p[i] = i + 1;
    }
    return p;
}

/* sum a buffer that Zig allocated through c_allocator (read across the seam). */
int sum_ints(int *p, int n) {
    int s = 0;
    for (int i = 0; i < n; i++) {
        s += p[i];
    }
    return s;
}

/* free a pointer Zig allocated through c_allocator (free across the seam). */
void take_and_free(int *p) {
    free(p);
}
