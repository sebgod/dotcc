/* exit: end the program as C does (C11 7.22.4.4): call the functions atexit registered, the last
   registered first (one that registers another while they run gets it called too), flush the
   streams once a stream that holds output back exists (the stdio layer sets
   __dotcc_flush_at_exit then), and end with _Exit, WASI's proc_exit. The program's entry
   (_start) calls exit with main's value when the program has it, which one that calls exit or
   atexit or opens a stream does; any other ends in proc_exit directly. The wat target's libc
   (WatLibc), compiled with the program.
   dotcc-libc: also defines atexit __dotcc_flush_at_exit */
#include <stdlib.h>

/* C requires room for at least 32 (C11 7.22.4.2). */
#define ATEXIT_MAX 32

static void (*funcs[ATEXIT_MAX])(void);
static int count;

void (*__dotcc_flush_at_exit)(void);

int atexit(void (*func)(void))
{
    if (count == ATEXIT_MAX)
    {
        return -1;
    }
    funcs[count++] = func;
    return 0;
}

void exit(int status)
{
    while (count > 0)
    {
        funcs[--count]();
    }
    if (__dotcc_flush_at_exit)
    {
        __dotcc_flush_at_exit();
    }
    _Exit(status);
}
