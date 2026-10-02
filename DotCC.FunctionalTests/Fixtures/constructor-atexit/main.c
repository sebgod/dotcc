/* GCC's constructor attribute in C23's namespaced spelling: the constructors run before main,
   those with the lower priority first, then the rest in definition order; the functions atexit
   registers run when main returns, the last registered first. */
#include <stdio.h>
#include <stdlib.h>

static void bye(void) { printf("atexit: bye\n"); }
static void bye_first(void) { printf("atexit: registered last, runs first\n"); }

[[gnu::constructor]] static void late(void) { printf("constructor: no priority\n"); }
[[gnu::constructor(101)]] static void early(void) { printf("constructor: priority 101\n"); }
[[gnu::constructor(200)]] static void middle(void) { printf("constructor: priority 200\n"); }

int main(void)
{
    if (atexit(bye) != 0 || atexit(bye_first) != 0) { return 1; }
    printf("main\n");
    return 0;
}
