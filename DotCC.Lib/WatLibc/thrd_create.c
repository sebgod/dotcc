/* thrd_create: start func(arg) on a new thread, with a stack and a TLS block of its own, through wasi-threads' thread-spawn; __dotcc_thread_main is where the new thread starts, once wasi_thread_start has pointed its stack and TLS block at the descriptor. The wat target's libc (WatLibc), compiled with the program. */
#include <threads_impl.h>
#include <stdlib.h>

/* The stack a thread gets. */
#define DOTCC_THREAD_STACK (256 * 1024)

void __dotcc_thread_main(int tid, struct __dotcc_thread *t)
{
    (void)tid;
    __dotcc_self = t;
    int r;
    if (setjmp(t->exit) == 0)
    {
        r = t->func(t->arg);
    }
    else
    {
        r = t->result;   /* thrd_exit */
    }
    if (__dotcc_tss.run_dtors) { __dotcc_tss.run_dtors(); }
    t->result = r;
    atomic_store((atomic_int *)&t->state, 1);
    __builtin_wasm_memory_atomic_notify(&t->state, 0xFFFFFFFFu);
}

int thrd_create(thrd_t *thr, thrd_start_t func, void *arg)
{
    struct __dotcc_thread *t = calloc(1, sizeof *t);
    char *stack = malloc(DOTCC_THREAD_STACK);
    void *tls = malloc(__builtin_dotcc_tls_size());
    if (t == NULL || stack == NULL || tls == NULL) { return thrd_nomem; }
    __builtin_dotcc_tls_init(tls);
    t->stack_top = (void *)((long)(stack + DOTCC_THREAD_STACK) & -16L);
    t->tls = tls;
    t->func = func;
    t->arg = arg;
    *(struct __dotcc_thread **)thr = t;
    if (__wasi_thread_spawn(t) < 0) { return thrd_error; }
    return thrd_success;
}
