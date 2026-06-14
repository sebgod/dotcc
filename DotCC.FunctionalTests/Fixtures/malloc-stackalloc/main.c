// Exercises the malloc -> stackalloc BUFFER peephole (V1).
//
// `char *buf = malloc(N)` with a compile-time-constant size, used only via
// buf[i] and free(buf), never escaping (passing buf[0]/buf[7] to printf is a
// subscript, not the bare pointer), is demoted to a zeroed stack buffer
// `byte* buf = stackalloc byte[N]` with the free dropped. The observable
// behavior is identical — every byte is written before it is read — so the
// committed snapshot is the same one a real compiler (which keeps the heap
// allocation) produces.

#include <stdio.h>
#include <stdlib.h>

int main(void) {
    char *buf = malloc(8);
    for (int i = 0; i < 8; i++) {
        buf[i] = (char)('A' + i);
    }

    long sum = 0;
    for (int i = 0; i < 8; i++) {
        sum += buf[i];
    }

    printf("first=%c last=%c sum=%ld\n", buf[0], buf[7], sum);
    free(buf);
    return 0;
}
