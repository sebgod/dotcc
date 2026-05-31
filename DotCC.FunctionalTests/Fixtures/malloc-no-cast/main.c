#include <stdio.h>
#include <stdlib.h>

/* Cast-less malloc — `T *p = malloc(...)` without the `(T*)` cast (valid C:
   void* implicitly converts to any object pointer). dotcc inserts the cast C#
   requires. A complex-arg malloc (a void* call) and a struct malloc that
   escapes via return (so it stays a heap pointer, not stack-promoted). */

struct Box { int v; };

struct Box *make(int v) {
    struct Box *b = malloc(sizeof(struct Box));  /* escapes via return → heap */
    b->v = v;
    return b;
}

int main(void) {
    int *a = malloc(4 * sizeof(int));
    for (int i = 0; i < 4; i++) a[i] = i * i;
    struct Box *b = make(42);
    printf("%d %d %d %d\n", a[0], a[1], a[2], a[3]);
    printf("%d\n", b->v);
    free(a);
    free(b);
    return 0;
}
