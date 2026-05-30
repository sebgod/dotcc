/* `restrict` (C99) — a pointer-only type qualifier promising no aliasing. dotcc
 * has no aliasing model, so it parses and drops it (the type is just the
 * pointer), exactly like const/volatile. A qualifier after the `*`
 * (`int *restrict`, `int * const`) parses too. gcc is the oracle. */
#include <stdio.h>

void copy(int *restrict dst, const int *restrict src, int n) {
    for (int i = 0; i < n; i++) dst[i] = src[i];
}

int main(void) {
    int a[3] = {1, 2, 3};
    int b[3];
    copy(b, a, 3);
    printf("%d %d %d\n", b[0], b[1], b[2]);
    return 0;
}
