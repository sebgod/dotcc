#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace DotCC.Libc;

/// <summary>
/// C11/C23 <c>&lt;threads.h&gt;</c> (§7.26) mapped onto
/// <see cref="System.Threading"/> — the <b>complete</b> surface: threads
/// (create/join/yield/current/equal/sleep/detach/exit), mutexes (plain /
/// recursive / timed), condition variables (<c>cnd_*</c>), thread-specific
/// storage (<c>tss_*</c> with per-thread-exit destructors), and
/// <c>call_once</c>. The opaque handle types <see cref="thrd_t"/> /
/// <see cref="mtx_t"/> / <see cref="cnd_t"/> / <see cref="tss_t"/> are blittable
/// value structs (a single id) so user code can stack-allocate them and take
/// their address; the managed objects live in side tables keyed by that id.
/// </summary>
/// <remarks>
/// <para>A C thread function returns <c>int</c>; .NET's <see cref="Thread"/>
/// has no return channel, so the result is stashed in the side table and
/// surfaced by <see cref="thrd_join"/>. The function pointer + arg are stored
/// as <see cref="IntPtr"/> and the body runs in a static entry method (a
/// lambda can't capture a function pointer).</para>
/// <para><b>Fidelity notes</b> (degraded, never silent): <c>tss_*</c>
/// destructors fire at the exit of a <c>thrd_create</c>-created thread but NOT
/// for the main / foreign threads (.NET has no per-thread-exit hook for those);
/// <c>thrd_exit</c> unwinds via a private sentinel exception caught in the entry
/// trampoline (there is no <c>Thread.Abort</c> on modern .NET), so it works from
/// a created thread but not the main thread; and <c>thrd_sleep</c> always sleeps
/// the full interval (dotcc has no async signal delivery, so it never reports a
/// remaining time). The timed calls take an absolute <c>TIME_UTC</c> deadline
/// (<c>mtx_timedlock</c> / <c>cnd_timedwait</c>) or a relative duration
/// (<c>thrd_sleep</c>), per C11.</para>
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>Opaque thread handle — just an id into <see cref="_threads"/>.</summary>
    public struct thrd_t { public int Id; }
    /// <summary>Opaque mutex handle — just an id into <see cref="_mtx"/>.</summary>
    public struct mtx_t { public int Id; }
    /// <summary>Opaque condition-variable handle — an id into <see cref="_cnd"/>.</summary>
    public struct cnd_t { public int Id; }
    /// <summary>Opaque thread-specific-storage key — an id into the per-thread
    /// value maps and the shared destructor table.</summary>
    public struct tss_t { public int Id; }

    // thrd_* return codes (values match glibc's <threads.h>).
    public const int thrd_success = 0;
    public const int thrd_busy = 1;
    public const int thrd_error = 2;
    public const int thrd_nomem = 3;
    public const int thrd_timedout = 4;

    // Mutex kinds. mtx_recursive is a flag bit that may be OR'd onto a base
    // (mtx_plain / mtx_timed), matching the C model.
    public const int mtx_plain = 0;
    public const int mtx_recursive = 1;
    public const int mtx_timed = 2;

    /// <summary>C11 <c>TSS_DTOR_ITERATIONS</c> — how many times the runtime retries
    /// thread-specific-storage destructors at thread exit (glibc's value).</summary>
    public const int TSS_DTOR_ITERATIONS = 4;

    private sealed class ThrdState
    {
        public IntPtr Func;   // (delegate*<void*,int>) stored as a pointer
        public IntPtr Arg;    // (void*)
        public int Result;
        public Thread? Thread;
        public bool Detached;
    }

    private sealed class MtxState
    {
        public int Type;
        public readonly SemaphoreSlim Avail = new(1, 1);
        public int Owner;   // managed-thread id of the current holder (recursive bookkeeping)
        public int Count;   // recursion depth
    }

    // A condition variable is a plain monitor object. cnd_wait enters this
    // monitor BEFORE releasing the caller's mutex, so a concurrent cnd_signal
    // (which must also enter the monitor to Pulse) cannot slip in between the
    // unlock and the wait — no lost wakeup, without reimplementing mtx.
    private sealed class CndState { public readonly object Lock = new(); }

    // thrd_exit unwinds the calling thread via this sentinel, caught in ThrdEntry
    // (modern .NET has no Thread.Abort). The exit code becomes the join result.
    private sealed class ThrdExitException : Exception { public int Code; }

    private static readonly ConcurrentDictionary<int, ThrdState> _threads = new();
    private static readonly ConcurrentDictionary<int, MtxState> _mtx = new();
    private static readonly ConcurrentDictionary<int, CndState> _cnd = new();
    // tss destructor per key (0 = none), shared across threads; the VALUES are
    // per-thread (each thread's own map, so distinct threads don't share slots).
    private static readonly ConcurrentDictionary<int, IntPtr> _tssDtors = new();
    [ThreadStatic] private static Dictionary<int, IntPtr>? _tssValues;
    // This thread's own thrd_t id (0 = not yet assigned). Set in the entry
    // trampoline for created threads; lazily minted by thrd_current otherwise.
    [ThreadStatic] private static int _selfThrdId;
    private static int _nextThrdId;
    private static int _nextMtxId;
    private static int _nextCndId;
    private static int _nextTssId;

    /// <summary>
    /// Spawn a thread running <paramref name="func"/>(<paramref name="arg"/>).
    /// The id is written into <paramref name="thr"/>; the int the function
    /// returns is captured for a later <see cref="thrd_join"/>.
    /// </summary>
    public static int thrd_create(thrd_t* thr, delegate*<void*, int> func, void* arg)
    {
        if (thr == null) { return thrd_error; }
        int id = Interlocked.Increment(ref _nextThrdId);
        var st = new ThrdState { Func = (IntPtr)func, Arg = (IntPtr)arg };
        st.Thread = new Thread(ThrdEntry);
        _threads[id] = st;
        thr->Id = id;
        st.Thread.Start(id);
        return thrd_success;
    }

    // Static entry — a lambda can't capture the function pointer, so the body
    // reads it from the side table by id (boxed through ParameterizedThreadStart).
    private static void ThrdEntry(object? idObj)
    {
        int id = (int)idObj!;
        var st = _threads[id];
        _selfThrdId = id;
        try
        {
            var f = (delegate*<void*, int>)st.Func;
            st.Result = f((void*)st.Arg);
        }
        catch (ThrdExitException ex)
        {
            st.Result = ex.Code;   // thrd_exit(res) inside the thread
        }
        finally
        {
            RunTssDtors();                                   // C11: dtors run at thread exit
            if (st.Detached) { _threads.TryRemove(id, out _); }
        }
    }

    // Run each thread-specific-storage destructor for a non-null value on this
    // thread, clearing the slot first (C11 §7.26.6.4), retrying up to
    // TSS_DTOR_ITERATIONS rounds since a dtor may set a fresh value.
    private static void RunTssDtors()
    {
        var vals = _tssValues;
        if (vals is null) { return; }
        for (var round = 0; round < TSS_DTOR_ITERATIONS; round++)
        {
            var any = false;
            foreach (var key in new List<int>(vals.Keys))
            {
                var v = vals[key];
                if (v == IntPtr.Zero) { continue; }
                if (!_tssDtors.TryGetValue(key, out var d) || d == IntPtr.Zero) { continue; }
                vals[key] = IntPtr.Zero;                     // clear BEFORE calling
                ((delegate*<void*, void>)d)((void*)v);
                any = true;
            }
            if (!any) { break; }
        }
    }

    // Convert a C11 absolute TIME_UTC deadline to a relative millisecond timeout
    // for the BCL wait primitives (clamped at 0 for an already-past deadline).
    private static int AbsTimeoutMs(timespec* ts)
    {
        long deadlineMs = ts->tv_sec * 1000L + ts->tv_nsec / 1_000_000L;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long rel = deadlineMs - nowMs;
        return rel <= 0 ? 0 : (rel > int.MaxValue ? int.MaxValue : (int)rel);
    }

    /// <summary>Wait for <paramref name="thr"/> to finish; write its result to
    /// <paramref name="res"/> when non-null.</summary>
    public static int thrd_join(thrd_t thr, int* res)
    {
        if (!_threads.TryGetValue(thr.Id, out var st) || st.Thread is null) { return thrd_error; }
        st.Thread.Join();
        if (res != null) { *res = st.Result; }
        _threads.TryRemove(thr.Id, out _);
        return thrd_success;
    }

    /// <summary>Hint to the scheduler to yield the current thread's slice.</summary>
    public static void thrd_yield() => Thread.Yield();

    /// <summary>Initialise a mutex of the given <paramref name="type"/>.</summary>
    public static int mtx_init(mtx_t* mtx, int type)
    {
        if (mtx == null) { return thrd_error; }
        int id = Interlocked.Increment(ref _nextMtxId);
        _mtx[id] = new MtxState { Type = type };
        mtx->Id = id;
        return thrd_success;
    }

    /// <summary>Block until the mutex is held by the calling thread.</summary>
    public static int mtx_lock(mtx_t* mtx)
    {
        if (!_mtx.TryGetValue(mtx->Id, out var st)) { return thrd_error; }
        int me = Environment.CurrentManagedThreadId;
        if ((st.Type & mtx_recursive) != 0 && st.Owner == me) { st.Count++; return thrd_success; }
        st.Avail.Wait();
        st.Owner = me;
        st.Count = 1;
        return thrd_success;
    }

    /// <summary>Acquire without blocking; <see cref="thrd_busy"/> if already held.</summary>
    public static int mtx_trylock(mtx_t* mtx)
    {
        if (!_mtx.TryGetValue(mtx->Id, out var st)) { return thrd_error; }
        int me = Environment.CurrentManagedThreadId;
        if ((st.Type & mtx_recursive) != 0 && st.Owner == me) { st.Count++; return thrd_success; }
        if (!st.Avail.Wait(0)) { return thrd_busy; }
        st.Owner = me;
        st.Count = 1;
        return thrd_success;
    }

    /// <summary>Release one level of ownership.</summary>
    public static int mtx_unlock(mtx_t* mtx)
    {
        if (!_mtx.TryGetValue(mtx->Id, out var st)) { return thrd_error; }
        if ((st.Type & mtx_recursive) != 0 && --st.Count > 0) { return thrd_success; }
        st.Owner = 0;
        st.Avail.Release();
        return thrd_success;
    }

    /// <summary>Destroy a mutex and release its resources.</summary>
    public static void mtx_destroy(mtx_t* mtx)
    {
        if (_mtx.TryRemove(mtx->Id, out var st)) { st.Avail.Dispose(); }
    }

    /// <summary>Block until the mutex is held, or until the absolute
    /// <see cref="TIME_UTC"/> deadline <paramref name="ts"/> passes
    /// (<see cref="thrd_timedout"/>). Recursive re-entry by the owner succeeds
    /// immediately, like <see cref="mtx_lock"/>.</summary>
    public static int mtx_timedlock(mtx_t* mtx, timespec* ts)
    {
        if (!_mtx.TryGetValue(mtx->Id, out var st)) { return thrd_error; }
        int me = Environment.CurrentManagedThreadId;
        if ((st.Type & mtx_recursive) != 0 && st.Owner == me) { st.Count++; return thrd_success; }
        if (!st.Avail.Wait(AbsTimeoutMs(ts))) { return thrd_timedout; }
        st.Owner = me;
        st.Count = 1;
        return thrd_success;
    }

    /// <summary>Return the handle of the calling thread. A thread not created by
    /// <see cref="thrd_create"/> (e.g. the main thread) is lazily assigned a
    /// handle on first call.</summary>
    public static thrd_t thrd_current()
    {
        if (_selfThrdId == 0)
        {
            int id = Interlocked.Increment(ref _nextThrdId);
            _threads[id] = new ThrdState { Thread = Thread.CurrentThread };
            _selfThrdId = id;
        }
        return new thrd_t { Id = _selfThrdId };
    }

    /// <summary>Non-zero iff <paramref name="a"/> and <paramref name="b"/> name the
    /// same thread.</summary>
    public static int thrd_equal(thrd_t a, thrd_t b) => a.Id == b.Id ? 1 : 0;

    /// <summary>Sleep for the RELATIVE duration <paramref name="duration"/>. dotcc
    /// has no async signal delivery, so the full interval always elapses; on the
    /// (never-taken) interrupted path C returns the remaining time — here
    /// <paramref name="remaining"/> is zeroed and 0 (success) is returned.</summary>
    public static int thrd_sleep(timespec* duration, timespec* remaining)
    {
        if (duration == null) { return -1; }
        long ms = duration->tv_sec * 1000L + duration->tv_nsec / 1_000_000L;
        if (ms < 0) { ms = 0; }
        Thread.Sleep(ms > int.MaxValue ? int.MaxValue : (int)ms);
        if (remaining != null) { remaining->tv_sec = 0; remaining->tv_nsec = 0; }
        return 0;
    }

    /// <summary>Terminate the calling thread with result <paramref name="res"/>
    /// (available to <see cref="thrd_join"/>). Implemented by unwinding to the entry
    /// trampoline via a sentinel exception — works from a <see cref="thrd_create"/>
    /// thread; from the main thread it would propagate uncaught (see the fidelity
    /// notes).</summary>
    public static void thrd_exit(int res) => throw new ThrdExitException { Code = res };

    /// <summary>Detach <paramref name="thr"/> — its storage is reclaimed when it
    /// finishes and it can no longer be joined. If it has already finished, its
    /// side-table entry is removed now.</summary>
    public static int thrd_detach(thrd_t thr)
    {
        if (!_threads.TryGetValue(thr.Id, out var st)) { return thrd_error; }
        st.Detached = true;
        if (st.Thread is { IsAlive: false }) { _threads.TryRemove(thr.Id, out _); }
        return thrd_success;
    }

    // ── condition variables ────────────────────────────────────────────
    /// <summary>Initialise a condition variable.</summary>
    public static int cnd_init(cnd_t* cond)
    {
        if (cond == null) { return thrd_error; }
        int id = Interlocked.Increment(ref _nextCndId);
        _cnd[id] = new CndState();
        cond->Id = id;
        return thrd_success;
    }

    /// <summary>Wake one thread waiting on <paramref name="cond"/>.</summary>
    public static int cnd_signal(cnd_t* cond)
    {
        if (!_cnd.TryGetValue(cond->Id, out var st)) { return thrd_error; }
        lock (st.Lock) { Monitor.Pulse(st.Lock); }
        return thrd_success;
    }

    /// <summary>Wake all threads waiting on <paramref name="cond"/>.</summary>
    public static int cnd_broadcast(cnd_t* cond)
    {
        if (!_cnd.TryGetValue(cond->Id, out var st)) { return thrd_error; }
        lock (st.Lock) { Monitor.PulseAll(st.Lock); }
        return thrd_success;
    }

    /// <summary>Atomically release <paramref name="mtx"/> and block on
    /// <paramref name="cond"/>; re-acquire <paramref name="mtx"/> before returning.
    /// Entering the condition's monitor before unlocking the mutex closes the
    /// lost-wakeup window (a signaller must enter the same monitor to Pulse).</summary>
    public static int cnd_wait(cnd_t* cond, mtx_t* mtx)
    {
        if (!_cnd.TryGetValue(cond->Id, out var st)) { return thrd_error; }
        Monitor.Enter(st.Lock);
        mtx_unlock(mtx);
        Monitor.Wait(st.Lock);
        Monitor.Exit(st.Lock);
        mtx_lock(mtx);
        return thrd_success;
    }

    /// <summary>As <see cref="cnd_wait"/>, but give up at the absolute
    /// <see cref="TIME_UTC"/> deadline <paramref name="ts"/>
    /// (<see cref="thrd_timedout"/>); the mutex is re-acquired either way.</summary>
    public static int cnd_timedwait(cnd_t* cond, mtx_t* mtx, timespec* ts)
    {
        if (!_cnd.TryGetValue(cond->Id, out var st)) { return thrd_error; }
        Monitor.Enter(st.Lock);
        mtx_unlock(mtx);
        bool signalled = Monitor.Wait(st.Lock, AbsTimeoutMs(ts));
        Monitor.Exit(st.Lock);
        mtx_lock(mtx);
        return signalled ? thrd_success : thrd_timedout;
    }

    /// <summary>Destroy a condition variable.</summary>
    public static void cnd_destroy(cnd_t* cond) => _cnd.TryRemove(cond->Id, out _);

    // ── thread-specific storage ────────────────────────────────────────
    /// <summary>Create a TSS key with an optional destructor
    /// (<c>delegate*&lt;void*, void&gt;</c>, may be null). Each thread's value for
    /// the key starts null.</summary>
    public static int tss_create(tss_t* key, delegate*<void*, void> dtor)
    {
        if (key == null) { return thrd_error; }
        int id = Interlocked.Increment(ref _nextTssId);
        _tssDtors[id] = (IntPtr)dtor;
        key->Id = id;
        return thrd_success;
    }

    /// <summary>The calling thread's value for <paramref name="key"/> (null if
    /// unset).</summary>
    public static void* tss_get(tss_t key)
    {
        var vals = _tssValues;
        if (vals != null && vals.TryGetValue(key.Id, out var v)) { return (void*)v; }
        return null;
    }

    /// <summary>Set the calling thread's value for <paramref name="key"/>. Does NOT
    /// invoke the destructor on the replaced value (C11 §7.26.6.1).</summary>
    public static int tss_set(tss_t key, void* val)
    {
        (_tssValues ??= new Dictionary<int, IntPtr>())[key.Id] = (IntPtr)val;
        return thrd_success;
    }

    /// <summary>Release <paramref name="key"/> — no destructors are called and no
    /// thread's stored value is changed (C11 §7.26.6.2).</summary>
    public static void tss_delete(tss_t key) => _tssDtors.TryRemove(key.Id, out _);

    // ── call_once ──────────────────────────────────────────────────────
    /// <summary>Run <paramref name="func"/> exactly once for a given
    /// <paramref name="flag"/>, even under concurrent callers; the others block
    /// until the winner finishes. <c>once_flag</c> is a plain <c>int</c>
    /// (0 = pending, 1 = running, 2 = done; <c>ONCE_FLAG_INIT</c> = 0).</summary>
    public static void call_once(int* flag, delegate*<void> func)
    {
        if (Volatile.Read(ref *flag) == 2) { return; }
        if (Interlocked.CompareExchange(ref *flag, 1, 0) == 0)
        {
            func();
            Volatile.Write(ref *flag, 2);
        }
        else
        {
            while (Volatile.Read(ref *flag) != 2) { Thread.Yield(); }
        }
    }
}
