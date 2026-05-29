/* C11 <threads.h> over .NET System.Threading. Four threads each increment a
 * shared counter 10000 times under a mutex; the total is 40000 regardless of
 * interleaving, so the output is deterministic (and the gcc oracle, built with
 * -pthread, agrees). The mutex is a `main` local passed to the workers via the
 * thread arg — that keeps `&lock` a stack address (no static-field address-of),
 * while the counter is a file-scope global (only ++, never address-taken).
 * The mutex is named `mux`, not `lock`: `lock` is a C# keyword and dotcc does
 * not yet @-escape colliding C identifiers (see C-SUPPORT.md). */
#include <stdio.h>
#include <stddef.h>
#include <threads.h>

#define NTHREADS 4
#define NITERS   10000

int counter = 0;

int worker(void* arg) {
    mtx_t* mux = (mtx_t*)arg;
    for (int i = 0; i < NITERS; i++) {
        mtx_lock(mux);
        counter++;
        mtx_unlock(mux);
    }
    return 0;
}

int main(void) {
    mtx_t mux;
    thrd_t threads[NTHREADS];

    mtx_init(&mux, mtx_plain);
    for (int i = 0; i < NTHREADS; i++) {
        thrd_create(&threads[i], &worker, &mux);
    }
    for (int i = 0; i < NTHREADS; i++) {
        thrd_join(threads[i], NULL);
    }
    mtx_destroy(&mux);

    printf("%d\n", counter);
    return 0;
}
