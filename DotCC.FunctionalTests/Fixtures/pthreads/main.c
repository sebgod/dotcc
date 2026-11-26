/* <pthread.h> on System.Threading: create/join with results, detached
   threads, statically initialized mutexes and condition variables, a
   recursive and an error-checking mutex, a timed wait on CLOCK_MONOTONIC,
   thread-specific data with a destructor, pthread_once and pthread_exit. */
#define _DEFAULT_SOURCE 1
#include <pthread.h>
#include <errno.h>
#include <stdio.h>
#include <stdint.h>
#include <time.h>

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t ready = PTHREAD_COND_INITIALIZER;
static long total;
static int done;

static void *worker(void *arg) {
    long n = (long)(intptr_t)arg;
    for (long i = 1; i <= n; i++) {
        pthread_mutex_lock(&lock);
        total += i;
        pthread_mutex_unlock(&lock);
    }
    return (void *)(intptr_t)(n * 2);
}

static void *finisher(void *arg) {
    (void)arg;
    pthread_mutex_lock(&lock);
    done = 1;
    pthread_cond_signal(&ready);
    pthread_mutex_unlock(&lock);
    return NULL;
}

static void *exiter(void *arg) {
    (void)arg;
    pthread_exit((void *)(intptr_t)99);
    return NULL;
}

static pthread_key_t key;
static int destroyed;
static void destroy(void *v) { destroyed += (int)(intptr_t)v; }
static void *keyed(void *arg) {
    pthread_setspecific(key, arg);
    return (void *)(intptr_t)(pthread_getspecific(key) == arg);
}

static int inits;
static pthread_once_t once = PTHREAD_ONCE_INIT;
static void init_once(void) { inits++; }

int main(void) {
    pthread_t t[4];
    for (int i = 0; i < 4; i++) pthread_create(&t[i], NULL, worker, (void *)(intptr_t)(1000 * (i + 1)));
    long sum = 0;
    for (int i = 0; i < 4; i++) {
        void *r;
        pthread_join(t[i], &r);
        sum += (long)(intptr_t)r;
    }
    printf("total %ld results %ld\n", total, sum);

    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_t f;
    pthread_mutex_lock(&lock);
    pthread_create(&f, &attr, finisher, NULL);
    pthread_attr_destroy(&attr);
    while (!done) pthread_cond_wait(&ready, &lock);
    pthread_mutex_unlock(&lock);
    printf("signalled %d\n", done);

    pthread_mutexattr_t ma;
    pthread_mutexattr_init(&ma);
    pthread_mutexattr_settype(&ma, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_t rec;
    pthread_mutex_init(&rec, &ma);
    pthread_mutex_lock(&rec);
    printf("recursive %d\n", pthread_mutex_lock(&rec));
    pthread_mutex_unlock(&rec);
    pthread_mutex_unlock(&rec);
    pthread_mutexattr_settype(&ma, PTHREAD_MUTEX_ERRORCHECK);
    pthread_mutex_t chk;
    pthread_mutex_init(&chk, &ma);
    pthread_mutex_lock(&chk);
    printf("errorcheck %d %d\n", pthread_mutex_lock(&chk) == EDEADLK, pthread_mutex_trylock(&chk) == EBUSY);
    pthread_mutex_unlock(&chk);
    printf("unlock unowned %d\n", pthread_mutex_unlock(&chk) == EPERM);
    pthread_mutexattr_destroy(&ma);

    pthread_condattr_t ca;
    pthread_condattr_init(&ca);
    pthread_condattr_setclock(&ca, CLOCK_MONOTONIC);
    pthread_cond_t timed;
    pthread_cond_init(&timed, &ca);
    pthread_condattr_destroy(&ca);
    struct timespec deadline;
    clock_gettime(CLOCK_MONOTONIC, &deadline);
    deadline.tv_nsec += 20 * 1000000;
    if (deadline.tv_nsec >= 1000000000) { deadline.tv_sec++; deadline.tv_nsec -= 1000000000; }
    pthread_mutex_lock(&lock);
    printf("timed out %d\n", pthread_cond_timedwait(&timed, &lock, &deadline) == ETIMEDOUT);
    pthread_mutex_unlock(&lock);
    pthread_cond_destroy(&timed);

    pthread_key_create(&key, destroy);
    pthread_t k;
    void *same;
    pthread_create(&k, NULL, keyed, (void *)(intptr_t)5);
    pthread_join(k, &same);
    printf("specific %d destroyed %d\n", (int)(intptr_t)same, destroyed);
    pthread_key_delete(key);

    pthread_t e;
    void *code;
    pthread_create(&e, NULL, exiter, NULL);
    pthread_join(e, &code);
    printf("exit %d\n", (int)(intptr_t)code);

    pthread_once(&once, init_once);
    pthread_once(&once, init_once);
    printf("once %d self %d\n", inits, pthread_equal(pthread_self(), pthread_self()) != 0);
    sched_yield();
    return 0;
}
