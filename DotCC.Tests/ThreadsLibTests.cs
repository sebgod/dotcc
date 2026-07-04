#nullable enable

using System.Runtime.InteropServices;
using System.Threading;
using DotCC.Libc;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Direct runtime tests for the C11 <c>&lt;threads.h&gt;</c> surface
/// (<see cref="DotCC.Libc.Libc"/> / ThreadsLib.cs) — the pieces that complete the
/// v1 subset: condition variables, thread-specific storage (with per-thread-exit
/// destructors), <c>call_once</c>, <c>thrd_current</c>/<c>equal</c>,
/// <c>thrd_exit</c>, <c>mtx_timedlock</c>, <c>thrd_sleep</c>, and
/// <c>timespec_get</c>. These call the runtime directly (like
/// <c>LibcWcharTests</c>); the end-to-end C→emit→run path is covered by the
/// <c>c11-threads</c> fixture. Every test is deterministic — synchronized by
/// join / condition variables, or by a timeout that provably cannot be satisfied
/// (a held mutex, an un-signalled condition) — so none is timing-flaky.
/// </summary>
[Collection("Threads")]
public sealed class ThreadsLibTests
{
    // ---- shared static thread bodies (a delegate*<void*,int> can't capture) ----
    private static int _onceCount;
    private static void OnceInit() => Interlocked.Increment(ref _onceCount);
    private static unsafe int OnceBody(void* arg) { call_once((int*)arg, &OnceInit); return 0; }

    private static tss_t _tssKey;
    private static int _tssDtorCount;
    private static unsafe void TssDtor(void* p) { NativeMemory.Free(p); Interlocked.Increment(ref _tssDtorCount); }
    private static unsafe int TssBody(void* arg)
    {
        void* p = NativeMemory.Alloc(4);
        tss_set(_tssKey, p);
        return tss_get(_tssKey) == p ? 1 : 0;
    }

    private static unsafe int ExitBody(void* arg) { thrd_exit(99); return 0; }

    // Timed-lock worker: arg is the mtx_t*; returns thrd_timedout (main holds it).
    private static unsafe int TimedlockBody(void* arg)
    {
        timespec ts;
        timespec_get(&ts, TIME_UTC);
        ts.tv_nsec += 30_000_000;   // +30 ms
        return mtx_timedlock((mtx_t*)arg, &ts);
    }

    // cnd_timedwait worker: arg carries both handles; returns thrd_timedout.
    private unsafe struct CwArgs { public cnd_t* C; public mtx_t* M; }
    private static unsafe int CwBody(void* arg)
    {
        var a = (CwArgs*)arg;
        timespec ts;
        timespec_get(&ts, TIME_UTC);
        ts.tv_nsec += 30_000_000;
        mtx_lock(a->M);
        int r = cnd_timedwait(a->C, a->M, &ts);   // nobody ever signals
        mtx_unlock(a->M);
        return r;
    }

    [Fact]
    public unsafe void call_once_runs_the_initializer_exactly_once_under_contention()
    {
        _onceCount = 0;
        int* flag = (int*)NativeMemory.AllocZeroed(4, 1);   // ONCE_FLAG_INIT == 0
        try
        {
            const int N = 8;
            var ts = stackalloc thrd_t[N];
            for (var i = 0; i < N; i++) { thrd_create(&ts[i], &OnceBody, flag).ShouldBe(thrd_success); }
            for (var i = 0; i < N; i++) { thrd_join(ts[i], null).ShouldBe(thrd_success); }
            _onceCount.ShouldBe(1);
        }
        finally { NativeMemory.Free(flag); }
    }

    [Fact]
    public unsafe void tss_round_trips_and_the_destructor_runs_at_thread_exit()
    {
        _tssDtorCount = 0;
        tss_t key;
        tss_create(&key, &TssDtor).ShouldBe(thrd_success);
        _tssKey = key;

        thrd_t t;
        thrd_create(&t, &TssBody, null).ShouldBe(thrd_success);
        int readBack = 0;
        thrd_join(t, &readBack).ShouldBe(thrd_success);

        readBack.ShouldBe(1);          // tss_get returned what tss_set stored
        _tssDtorCount.ShouldBe(1);     // dtor fired once at the thread's exit
        tss_delete(key);
    }

    [Fact]
    public unsafe void thrd_exit_result_is_delivered_to_join()
    {
        thrd_t t;
        thrd_create(&t, &ExitBody, null).ShouldBe(thrd_success);
        int res = 0;
        thrd_join(t, &res).ShouldBe(thrd_success);
        res.ShouldBe(99);
    }

    [Fact]
    public unsafe void thrd_current_is_stable_and_thrd_equal_matches()
    {
        var a = thrd_current();
        var b = thrd_current();
        thrd_equal(a, b).ShouldBe(1);
    }

    [Fact]
    public unsafe void mtx_timedlock_times_out_while_the_mutex_is_held()
    {
        // The current thread holds the mutex for the whole test, so a timed lock
        // from a worker MUST time out — independent of wall-clock speed.
        mtx_t m;
        mtx_init(&m, mtx_timed).ShouldBe(thrd_success);
        mtx_lock(&m).ShouldBe(thrd_success);
        try
        {
            thrd_t w;
            thrd_create(&w, &TimedlockBody, &m).ShouldBe(thrd_success);
            int result = 0;
            thrd_join(w, &result).ShouldBe(thrd_success);
            result.ShouldBe(thrd_timedout);
        }
        finally { mtx_unlock(&m); mtx_destroy(&m); }
    }

    [Fact]
    public unsafe void cnd_timedwait_times_out_with_no_signal()
    {
        mtx_t m; cnd_t c;
        mtx_init(&m, mtx_plain);
        cnd_init(&c);
        CwArgs args; args.C = &c; args.M = &m;
        thrd_t w;
        thrd_create(&w, &CwBody, &args).ShouldBe(thrd_success);
        int result = 0;
        thrd_join(w, &result).ShouldBe(thrd_success);
        result.ShouldBe(thrd_timedout);
        cnd_destroy(&c);
        mtx_destroy(&m);
    }

    [Fact]
    public unsafe void thrd_sleep_returns_success_for_a_short_duration()
    {
        timespec dur;
        dur.tv_sec = 0;
        dur.tv_nsec = 1_000_000;   // 1 ms
        thrd_sleep(&dur, null).ShouldBe(0);
    }

    [Fact]
    public unsafe void timespec_get_reports_TIME_UTC()
    {
        timespec ts;
        timespec_get(&ts, TIME_UTC).ShouldBe(TIME_UTC);
        ts.tv_sec.ShouldBeGreaterThan(0);
        ts.tv_nsec.ShouldBeInRange(0, 999_999_999);
    }
}
