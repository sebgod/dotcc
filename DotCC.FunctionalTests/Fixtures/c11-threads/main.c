/* C11 <threads.h> — the full surface on .NET System.Threading. Each phase is
   made deterministic (threads joined / condition-synchronized before the result
   is printed, all printf from main) so stdout is stable across runs. */
#include <threads.h>
#include <stdio.h>
#include <stdlib.h>

/* ---- phase 1: mutex-protected counter ---- */
static mtx_t cnt_mtx;
static int counter = 0;
static int inc_worker(void* arg) {
    for (int i = 0; i < 1000; i++) {
        mtx_lock(&cnt_mtx);
        counter++;
        mtx_unlock(&cnt_mtx);
    }
    return 0;
}

/* ---- phase 2: call_once ---- */
static once_flag once_f = ONCE_FLAG_INIT;
static int once_count = 0;
static void once_init(void) { once_count++; }
static int once_worker(void* arg) { call_once(&once_f, &once_init); return 0; }

/* ---- phase 3: condition variable (producer/consumer) ---- */
static mtx_t c_mtx;
static cnd_t c_cv;
static int ready = 0;
static int shared_val = 0;
static int consumer(void* arg) {
    mtx_lock(&c_mtx);
    while (ready == 0) { cnd_wait(&c_cv, &c_mtx); }
    int v = shared_val;
    mtx_unlock(&c_mtx);
    return v * 2;
}

/* ---- phase 4: thread-specific storage + destructor ---- */
static tss_t key;
static int dtor_count = 0;
static int tss_readback = 0;
static void key_dtor(void* p) { dtor_count++; free(p); }
static int tss_worker(void* arg) {
    int* box = (int*)malloc(sizeof(int));
    *box = 7;
    tss_set(key, box);
    int* got = (int*)tss_get(key);
    tss_readback = *got;
    return 0;   /* key_dtor runs at thread exit */
}

/* ---- phase 5: thrd_current / thrd_equal ---- */
static int cur_worker(void* arg) {
    thrd_t a = thrd_current();
    thrd_t b = thrd_current();
    return thrd_equal(a, b);
}

/* ---- phase 6: thrd_exit ---- */
static int exit_worker(void* arg) {
    thrd_exit(42);
    return 0;   /* not reached */
}

/* ---- phase 7: mtx_timedlock (times out on a held mutex) ---- */
static mtx_t tl_mtx;
static int tl_result = 0;
static int timedlock_worker(void* arg) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    ts.tv_nsec += 50000000;   /* +50 ms deadline */
    int r = mtx_timedlock(&tl_mtx, &ts);
    tl_result = r;
    if (r == thrd_success) { mtx_unlock(&tl_mtx); }
    return 0;
}

/* ---- phase 8: cnd_timedwait (times out with no signal) ---- */
static mtx_t cw_mtx;
static cnd_t cw_cv;
static int cw_result = 0;
static int cndwait_worker(void* arg) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    ts.tv_nsec += 50000000;
    mtx_lock(&cw_mtx);
    cw_result = cnd_timedwait(&cw_cv, &cw_mtx, &ts);
    mtx_unlock(&cw_mtx);
    return 0;
}

/* ---- phase 10: thrd_detach (synchronized via a condition var) ---- */
static mtx_t d_mtx;
static cnd_t d_cv;
static int d_done = 0;
static int detach_worker(void* arg) {
    mtx_lock(&d_mtx);
    d_done = 1;
    cnd_signal(&d_cv);
    mtx_unlock(&d_mtx);
    return 0;
}

int main(void) {
    /* phase 1 */
    mtx_init(&cnt_mtx, mtx_plain);
    thrd_t ts4[4];
    for (int i = 0; i < 4; i++) { thrd_create(&ts4[i], &inc_worker, NULL); }
    for (int i = 0; i < 4; i++) { thrd_join(ts4[i], NULL); }
    mtx_destroy(&cnt_mtx);
    printf("sum=%d\n", counter);

    /* phase 2 */
    thrd_t to[4];
    for (int i = 0; i < 4; i++) { thrd_create(&to[i], &once_worker, NULL); }
    for (int i = 0; i < 4; i++) { thrd_join(to[i], NULL); }
    printf("once=%d\n", once_count);

    /* phase 3 */
    mtx_init(&c_mtx, mtx_plain);
    cnd_init(&c_cv);
    thrd_t tc;
    thrd_create(&tc, &consumer, NULL);
    mtx_lock(&c_mtx);
    shared_val = 42;
    ready = 1;
    cnd_signal(&c_cv);
    mtx_unlock(&c_mtx);
    int cres = 0;
    thrd_join(tc, &cres);
    cnd_destroy(&c_cv);
    mtx_destroy(&c_mtx);
    printf("cnd=%d\n", cres);

    /* phase 4 */
    tss_create(&key, &key_dtor);
    thrd_t tk;
    thrd_create(&tk, &tss_worker, NULL);
    thrd_join(tk, NULL);
    tss_delete(key);
    printf("tss=%d dtor=%d\n", tss_readback, dtor_count);

    /* phase 5 */
    thrd_t tcur;
    thrd_create(&tcur, &cur_worker, NULL);
    int eqres = 0;
    thrd_join(tcur, &eqres);
    printf("equal=%d\n", eqres);

    /* phase 6 */
    thrd_t te;
    thrd_create(&te, &exit_worker, NULL);
    int exres = 0;
    thrd_join(te, &exres);
    printf("exit=%d\n", exres);

    /* phase 7 */
    mtx_init(&tl_mtx, mtx_timed);
    mtx_lock(&tl_mtx);
    thrd_t tt;
    thrd_create(&tt, &timedlock_worker, NULL);
    thrd_join(tt, NULL);
    mtx_unlock(&tl_mtx);
    mtx_destroy(&tl_mtx);
    printf("timedlock=%d\n", tl_result);

    /* phase 8 */
    mtx_init(&cw_mtx, mtx_plain);
    cnd_init(&cw_cv);
    thrd_t tw;
    thrd_create(&tw, &cndwait_worker, NULL);
    thrd_join(tw, NULL);
    cnd_destroy(&cw_cv);
    mtx_destroy(&cw_mtx);
    printf("cndwait=%d\n", cw_result);

    /* phase 9: thrd_sleep (relative duration) */
    struct timespec dur;
    dur.tv_sec = 0;
    dur.tv_nsec = 1000000;   /* 1 ms */
    int sr = thrd_sleep(&dur, NULL);
    printf("sleep=%d\n", sr);

    /* phase 10 */
    mtx_init(&d_mtx, mtx_plain);
    cnd_init(&d_cv);
    thrd_t td;
    thrd_create(&td, &detach_worker, NULL);
    thrd_detach(td);
    mtx_lock(&d_mtx);
    while (d_done == 0) { cnd_wait(&d_cv, &d_mtx); }
    mtx_unlock(&d_mtx);
    cnd_destroy(&d_cv);
    mtx_destroy(&d_mtx);
    printf("detach=ok\n");

    return 0;
}
