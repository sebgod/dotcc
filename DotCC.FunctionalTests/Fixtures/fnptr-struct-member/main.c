/* Function-pointer struct members — `Ret (*name)(params);` inside a struct.
 * The struct-member counterpart of fn-ptr typedefs/params; ubiquitous in C for
 * vtables and callback/dispatch tables (chibi-scheme's
 * `struct sigaction { void (*sa_handler)(int); … }`). dotcc lowers each to a
 * C# `delegate*` field. */
#include <stdio.h>

/* A tiny dispatch table: two operations selected by name. */
struct ops {
    int (*add)(int, int);
    int (*mul)(int, int);
    void (*greet)(void);     /* no-arg fn-ptr member */
};

static int do_add(int a, int b) { return a + b; }
static int do_mul(int a, int b) { return a * b; }
static void do_greet(void) { printf("hi\n"); }

int main(void) {
    struct ops t;
    t.add = do_add;
    t.mul = do_mul;
    t.greet = do_greet;
    t.greet();
    printf("%d %d\n", t.add(3, 4), t.mul(3, 4));   /* 7 12 */
    return 0;
}
