#ifndef _PTHREAD_H
#define _PTHREAD_H

/* dotcc's <pthread.h>: POSIX threads on System.Threading (DotCC.Libc's
   PthreadLib), sharing the mutex, condition-variable and thread-specific-
   storage tables of C11's <threads.h>. The types are glibc's names with
   layouts of dotcc's own; the runtime reads the fields by those layouts.

   - Threads are .NET background threads, so a program ends when main returns
     or calls exit(), with its threads, as in C. pthread_t is the same id
     <threads.h>'s thrd_current gives. A thread's stack size comes from its
     attributes (8 MiB, glibc's default, when none is set).
   - A mutex or condition variable needs no init: the static initializers are
     zeros and the first use creates it. Normal, recursive and error-checking
     mutexes behave as POSIX describes; a default one is a normal one.
   - pthread_cond_timedwait reads its deadline on the condition's clock:
     CLOCK_REALTIME unless pthread_condattr_setclock chose CLOCK_MONOTONIC.
   - pthread_exit from a created thread ends it with that result. Key
     destructors run when a created thread ends, as <threads.h>'s do. */

#include <sched.h>
#include <sys/types.h>
#include <time.h>

typedef unsigned long pthread_t;

typedef struct {
    int __detachstate;
    unsigned long __stacksize;
    void *__stackaddr;
} pthread_attr_t;

typedef struct { int __id; int __kind; } pthread_mutex_t;
typedef struct { int __kind; } pthread_mutexattr_t;
typedef struct { int __id; int __clock; } pthread_cond_t;
typedef struct { int __clock; } pthread_condattr_t;
typedef unsigned int pthread_key_t;
typedef int pthread_once_t;

#define PTHREAD_MUTEX_INITIALIZER { 0, 0 }
#define PTHREAD_COND_INITIALIZER { 0, 0 }
#define PTHREAD_ONCE_INIT 0

#define PTHREAD_CREATE_JOINABLE 0
#define PTHREAD_CREATE_DETACHED 1

#define PTHREAD_MUTEX_NORMAL     0
#define PTHREAD_MUTEX_RECURSIVE  1
#define PTHREAD_MUTEX_ERRORCHECK 2
#define PTHREAD_MUTEX_DEFAULT    PTHREAD_MUTEX_NORMAL

#define PTHREAD_STACK_MIN 16384
#define PTHREAD_KEYS_MAX  1024

int pthread_create(pthread_t *thread, const pthread_attr_t *attr, void *(*start)(void *), void *arg);
int pthread_join(pthread_t thread, void **retval);
int pthread_detach(pthread_t thread);
pthread_t pthread_self(void);
int pthread_equal(pthread_t a, pthread_t b);
void pthread_exit(void *retval);
int pthread_once(pthread_once_t *once, void (*init)(void));

int pthread_attr_init(pthread_attr_t *attr);
int pthread_attr_destroy(pthread_attr_t *attr);
int pthread_attr_setdetachstate(pthread_attr_t *attr, int state);
int pthread_attr_getdetachstate(const pthread_attr_t *attr, int *state);
int pthread_attr_setstacksize(pthread_attr_t *attr, size_t size);
int pthread_attr_getstacksize(const pthread_attr_t *attr, size_t *size);
int pthread_attr_getstack(const pthread_attr_t *attr, void **addr, size_t *size);

int pthread_mutexattr_init(pthread_mutexattr_t *attr);
int pthread_mutexattr_destroy(pthread_mutexattr_t *attr);
int pthread_mutexattr_settype(pthread_mutexattr_t *attr, int kind);
int pthread_mutexattr_gettype(const pthread_mutexattr_t *attr, int *kind);
int pthread_mutex_init(pthread_mutex_t *mutex, const pthread_mutexattr_t *attr);
int pthread_mutex_destroy(pthread_mutex_t *mutex);
int pthread_mutex_lock(pthread_mutex_t *mutex);
int pthread_mutex_trylock(pthread_mutex_t *mutex);
int pthread_mutex_timedlock(pthread_mutex_t *mutex, const struct timespec *abstime);
int pthread_mutex_unlock(pthread_mutex_t *mutex);

int pthread_condattr_init(pthread_condattr_t *attr);
int pthread_condattr_destroy(pthread_condattr_t *attr);
int pthread_condattr_setclock(pthread_condattr_t *attr, clockid_t clock);
int pthread_condattr_getclock(const pthread_condattr_t *attr, clockid_t *clock);
int pthread_cond_init(pthread_cond_t *cond, const pthread_condattr_t *attr);
int pthread_cond_destroy(pthread_cond_t *cond);
int pthread_cond_signal(pthread_cond_t *cond);
int pthread_cond_broadcast(pthread_cond_t *cond);
int pthread_cond_wait(pthread_cond_t *cond, pthread_mutex_t *mutex);
int pthread_cond_timedwait(pthread_cond_t *cond, pthread_mutex_t *mutex, const struct timespec *abstime);

int pthread_key_create(pthread_key_t *key, void (*destructor)(void *));
int pthread_key_delete(pthread_key_t key);
void *pthread_getspecific(pthread_key_t key);
int pthread_setspecific(pthread_key_t key, const void *value);

#endif /* _PTHREAD_H */
