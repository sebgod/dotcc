#ifndef _DOTCC_THREADS_IMPL_H
#define _DOTCC_THREADS_IMPL_H

/* What the wat target's C11 threads share. A thread is an instance of the module over the shared
   memory (wasi-threads): thrd_create gives __wasi_thread_spawn a descriptor, the host runs
   wasi_thread_start in a new instance, and that points the thread's stack and TLS block at the
   descriptor's first two fields before it runs __dotcc_thread_main. Blocking is a futex: wasm's
   memory.atomic.wait32 and notify, through clang's builtin names. */

#include <threads.h>
#include <stdatomic.h>
#include <setjmp.h>
#include <time.h>
#include <wasi.h>

/* Wait while *addr == expected, at most timeout_ns (negative: no limit): 0 woken, 1 not equal,
   2 timed out. Wake up to count waiters on addr; returns how many. */
int __builtin_wasm_memory_atomic_wait32(int *addr, int expected, long long timeout_ns);
unsigned __builtin_wasm_memory_atomic_notify(int *addr, unsigned count);

/* A thread's TLS block: its size, and its thread-locals' initial values copied into it. */
unsigned __builtin_dotcc_tls_size(void);
void __builtin_dotcc_tls_init(void *block);

/* A thread. stack_top and tls first: wasi_thread_start reads them (eight bytes each). */
struct __dotcc_thread {
    void *stack_top;
    void *tls;
    thrd_start_t func;
    void *arg;
    int result;
    int state;      /* 0 running, 1 finished: what thrd_join waits on */
    int detached;
    jmp_buf exit;   /* thrd_exit unwinds to the thread's entry */
};

/* The running thread (NULL on the main thread). */
extern _Thread_local struct __dotcc_thread *__dotcc_self;

/* A mutex (an mtx_t's 16 bytes): lock is 0 free, 1 held, 2 held with waiters. */
struct __dotcc_mtx {
    int lock;
    int type;
    int owner;
    int count;
};

/* A condition (a cnd_t's 16 bytes): a sequence number a waiter sleeps on. */
struct __dotcc_cnd {
    int seq;
    int waiters;
    int pad[2];
};

/* The thread-specific storage keys: each key's destructor, and the running thread's values. */
#define DOTCC_TSS_KEYS 64
extern struct __dotcc_tss_t {
    int next;
    tss_dtor_t dtors[DOTCC_TSS_KEYS];
    void (*run_dtors)(void);
} __dotcc_tss;
/* (in a struct: the binder takes no _Thread_local array) */
extern _Thread_local struct __dotcc_tss_values_t { void *v[DOTCC_TSS_KEYS]; } __dotcc_tss_values;

/* What a mutex records as its owner: the running thread's descriptor, -1 on the main thread. */
static inline int __dotcc_thread_id(void)
{
    return __dotcc_self ? (int)(long)__dotcc_self : -1;
}

/* Nanoseconds from now until the TIME_UTC deadline (none or negative: it has passed). */
static inline long long __dotcc_ns_until(const struct timespec *deadline)
{
    unsigned long now = 0;
    __wasi_clock_time_get(__WASI_CLOCKID_REALTIME, 1, &now);
    return (long long)deadline->tv_sec * 1000000000LL + deadline->tv_nsec - (long long)now;
}

#endif
