#ifndef _THREADS_H
#define _THREADS_H

/* dotcc's <threads.h> — C11 7.26 / C23 threads, implemented in full on top of
   .NET System.Threading (see DotCC.Libc/ThreadsLib.cs).

   Complete surface: thrd_* (create/join/yield/current/equal/sleep/detach/exit),
   mtx_* (init/lock/timedlock/trylock/unlock/destroy), cnd_* (init/signal/
   broadcast/wait/timedwait/destroy), tss_* (create/get/set/delete, with
   per-thread-exit destructors), and call_once.

   Fidelity notes (degraded, never silent; see C-SUPPORT.md): tss_* destructors
   fire at the exit of a thrd_create-created thread but not the main/foreign
   threads; thrd_exit works from a created thread (it unwinds to the runtime's
   entry trampoline); thrd_sleep always sleeps the full interval (no async
   signals). The timed calls take an absolute TIME_UTC deadline (mtx_timedlock /
   cnd_timedwait) or a relative duration (thrd_sleep), per C11.

   thrd_t / mtx_t / cnd_t / tss_t are opaque handle types backed by Libc side
   tables. They are pre-registered as known type names in dotcc's TypeNameRewriter
   (Compiler.PredefinedTypeNames), so no typedef is needed here — they resolve to
   Libc.thrd_t / Libc.mtx_t / Libc.cnd_t / Libc.tss_t through `using static`. */

#include <time.h>   /* struct timespec + TIME_UTC, for the timed calls */

/* Thread start routine: int func(void* arg). dotcc lowers this typedef to a
   C# function-pointer alias `delegate*<void*, int>`, and `&my_func` at the
   call site to the matching function pointer. */
typedef int (*thrd_start_t)(void* arg);

/* TSS destructor: void dtor(void* value) -> `delegate*<void*, void>`. */
typedef void (*tss_dtor_t)(void* val);

/* once_flag is a plain int (glibc's choice); ONCE_FLAG_INIT initialises it. */
typedef int once_flag;
#define ONCE_FLAG_INIT 0

/* thrd_* return codes. */
#define thrd_success  0
#define thrd_busy     1
#define thrd_error    2
#define thrd_nomem    3
#define thrd_timedout 4

/* Mutex kinds (mtx_recursive is a flag bit that may be OR'd onto a base). */
#define mtx_plain     0
#define mtx_recursive 1
#define mtx_timed     2

/* Retry count for TSS destructors at thread exit. */
#define TSS_DTOR_ITERATIONS 4

int    thrd_create(thrd_t* thr, thrd_start_t func, void* arg);
thrd_t thrd_current(void);
int    thrd_equal(thrd_t a, thrd_t b);
int    thrd_sleep(const struct timespec* duration, struct timespec* remaining);
void   thrd_yield(void);
void   thrd_exit(int res);
int    thrd_detach(thrd_t thr);
int    thrd_join(thrd_t thr, int* res);

int    mtx_init(mtx_t* mtx, int type);
int    mtx_lock(mtx_t* mtx);
int    mtx_timedlock(mtx_t* mtx, const struct timespec* ts);
int    mtx_trylock(mtx_t* mtx);
int    mtx_unlock(mtx_t* mtx);
void   mtx_destroy(mtx_t* mtx);

int    cnd_init(cnd_t* cond);
int    cnd_signal(cnd_t* cond);
int    cnd_broadcast(cnd_t* cond);
int    cnd_wait(cnd_t* cond, mtx_t* mtx);
int    cnd_timedwait(cnd_t* cond, mtx_t* mtx, const struct timespec* ts);
void   cnd_destroy(cnd_t* cond);

int    tss_create(tss_t* key, tss_dtor_t dtor);
void*  tss_get(tss_t key);
int    tss_set(tss_t key, void* val);
void   tss_delete(tss_t key);

void   call_once(once_flag* flag, void (*func)(void));

/* C11 7.26.1: thread_local expands to _Thread_local (-> [ThreadStatic] on the
   emitted global's field). C23 makes lowercase thread_local a first-class
   keyword (promoted by DialectKeywordRewriter) and deprecates the macro, so -
   like <assert.h>'s static_assert and <stdnoreturn.h>'s noreturn - it is only
   defined for C11 <= __STDC_VERSION__ < C23. */
#if defined __STDC_VERSION__ && __STDC_VERSION__ >= 201112L && __STDC_VERSION__ < 202311L
#define thread_local _Thread_local
#endif

#endif
