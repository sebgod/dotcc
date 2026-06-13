#include <stdio.h>

/* Faithful `volatile` lowering (phase V1): a volatile scalar lvalue reads through
 * Volatile.Read and writes through Volatile.Write (acquire/release fences) instead
 * of having the qualifier erased. Single-threaded, the observable result is the
 * same as a plain access, so this fixture pins the values — the point is that the
 * emitted C# routes every read/write of a volatile object through the Volatile API
 * (verified by the unit tests + the emit inspection). */

struct Ctl {
    volatile int trap;   /* the Lua `volatile sig_atomic_t trap;` shape */
    int count;
};

static void arm(struct Ctl *c, int v) {
    c->trap = v;         /* Volatile.Write through a pointer */
}

int main(void) {
    volatile int flag = 0;
    flag = 1;            /* Volatile.Write */
    flag += 10;          /* fenced read-modify-write */
    flag++;              /* fenced ++ */

    int volatile east = 100; /* east/postfix `int volatile` — same fenced lowering */
    east += 5;               /* fenced read-modify-write through the postfix form */

    struct Ctl c;
    c.trap = 0;
    c.count = 0;
    arm(&c, 5);
    c.count += c.trap;   /* read a volatile field, write a plain field */

    int snapshot = flag; /* Volatile.Read */
    printf("flag=%d trap=%d count=%d\n", flag, c.trap, c.count);
    printf("snapshot=%d east=%d\n", snapshot, east);
    return 0;
}
