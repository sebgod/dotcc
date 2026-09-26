/* A user type may share a name with a type the runtime uses: `Lock` is also System.Threading.Lock,
   which the runtime's file, environ and locale tables lock on. The runtime spells it qualified, so the
   user's struct shadows nothing. */
#include <stdio.h>

typedef struct Lock { int held; int owner; } Lock;

static void acquire(Lock *l, int who) { l->held = 1; l->owner = who; }

int main(void) {
    Lock l = { 0, 0 };
    acquire(&l, 42);
    printf("held=%d owner=%d\n", l.held, l.owner);
    return 0;
}
