#ifndef _THREADS_H
#define _THREADS_H

/* dotcc's <threads.h> — C11 7.26 / C23 threads, implemented on top of
   .NET System.Threading (see DotCC.Libc/ThreadsLib.cs).

   v1 subset: thrd_create / thrd_join / thrd_yield and
   mtx_init / mtx_lock / mtx_trylock / mtx_unlock / mtx_destroy.
   Deferred (raise no error if unused): cnd_*, tss_*, call_once,
   thrd_current/thrd_equal/thrd_sleep/thrd_detach/thrd_exit, mtx_timedlock.

   thrd_t and mtx_t are opaque handle types backed by Libc side tables.
   They are pre-registered as known type names in dotcc's TypeNameRewriter
   (Compiler.PredefinedTypeNames), so no typedef is needed here — they
   resolve to Libc.thrd_t / Libc.mtx_t through the emitted `using static`. */

/* Thread start routine: int func(void* arg). dotcc lowers this typedef to a
   C# function-pointer alias `delegate*<void*, int>`, and `&my_func` at the
   call site to the matching function pointer. */
typedef int (*thrd_start_t)(void* arg);

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

int  thrd_create(thrd_t* thr, thrd_start_t func, void* arg);
int  thrd_join(thrd_t thr, int* res);
void thrd_yield(void);

int  mtx_init(mtx_t* mtx, int type);
int  mtx_lock(mtx_t* mtx);
int  mtx_trylock(mtx_t* mtx);
int  mtx_unlock(mtx_t* mtx);
void mtx_destroy(mtx_t* mtx);

/* C11 7.26.1: thread_local expands to _Thread_local (-> [ThreadStatic] on the
   emitted global's field). C23 makes lowercase thread_local a first-class
   keyword (promoted by DialectKeywordRewriter) and deprecates the macro, so -
   like <assert.h>'s static_assert and <stdnoreturn.h>'s noreturn - it is only
   defined for C11 <= __STDC_VERSION__ < C23. */
#if defined __STDC_VERSION__ && __STDC_VERSION__ >= 201112L && __STDC_VERSION__ < 202311L
#define thread_local _Thread_local
#endif

#endif
