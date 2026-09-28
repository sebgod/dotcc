/* A thread-local with a non-zero initial value (C11 6.2.4p4): every thread's
   instance starts at that value, not only the first thread's, and a NULL
   initializer is the zero value (CPython's `_Py_thread_local PyThreadState
   *_Py_tss_tstate = NULL;`, pystate.c). The workers run one after the other, so
   the output is deterministic. */
#include <stdio.h>
#include <stddef.h>
#include <threads.h>

_Thread_local int counter = 7;
_Thread_local const char *label = "main";
_Thread_local int *current = NULL;

static int worker(void *arg)
{
    int bump = *(int *)arg;
    int start = counter;
    counter += bump;
    label = "worker";
    current = &counter;
    return start * 100 + counter + (current == &counter);
}

int main(void)
{
    int a = 1, b = 2;
    int r1 = 0, r2 = 0;
    thrd_t t1, t2;
    int *p;
    thrd_create(&t1, worker, &a);
    thrd_join(t1, &r1);
    thrd_create(&t2, worker, &b);
    thrd_join(t2, &r2);
    printf("%d %d\n", r1, r2);
    printf("%d %s %d\n", counter, label, current == NULL);
    counter++;
    p = &counter;
    *p += 10;
    printf("%d\n", counter);
    return 0;
}
