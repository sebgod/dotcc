/* C11 _Thread_local — thread storage duration -> [ThreadStatic] on the
 * emitted global's field. Two worker threads each bump THEIR OWN
 * thread-local counter (different rep counts), so the "shared global"
 * would race and sum to 1250 — but each thread sees a distinct
 * zero-initialized slot, and main's own slot is untouched by the workers.
 * (dotcc supports zero/default-initialized thread-locals only; a non-zero
 * initializer is a documented compile error.) */
#include <stdio.h>
#include <threads.h>

_Thread_local int tls_count;

static int worker(void *arg) {
    int reps = *(int *)arg;
    for (int i = 0; i < reps; i++) tls_count++;
    return tls_count;   /* this thread's own final value */
}

int main(void) {
    int r1 = 1000, r2 = 250;
    thrd_t t1, t2;
    thrd_create(&t1, worker, &r1);
    thrd_create(&t2, worker, &r2);
    int v1 = 0, v2 = 0;
    thrd_join(t1, &v1);
    thrd_join(t2, &v2);
    tls_count += 5;
    printf("%d %d %d\n", v1, v2, tls_count);
    return 0;
}
