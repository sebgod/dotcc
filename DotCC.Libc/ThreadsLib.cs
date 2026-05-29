#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DotCC.Libc;

/// <summary>
/// C11/C23 <c>&lt;threads.h&gt;</c> (§7.26) mapped onto
/// <see cref="System.Threading"/>. v1 subset: thread create/join/yield and
/// mutexes (plain / recursive / timed-as-plain). The opaque handle types
/// <see cref="thrd_t"/> / <see cref="mtx_t"/> are blittable value structs (a
/// single id) so user code can stack-allocate them and take their address
/// (<c>thrd_t t; thrd_create(&amp;t, …)</c>); the managed objects live in
/// side tables keyed by that id.
/// </summary>
/// <remarks>
/// <para><b>Deferred (documented in C-SUPPORT.md):</b> condition variables
/// (<c>cnd_*</c>), thread-specific storage (<c>tss_*</c>), <c>call_once</c>,
/// <c>thrd_current</c>/<c>thrd_equal</c> (need a managed-thread→handle map),
/// <c>thrd_sleep</c> (needs <c>struct timespec</c>), <c>thrd_detach</c>,
/// <c>thrd_exit</c>, and <c>mtx_timedlock</c>.</para>
/// <para>A C thread function returns <c>int</c>; .NET's <see cref="Thread"/>
/// has no return channel, so the result is stashed in the side table and
/// surfaced by <see cref="thrd_join"/>. The function pointer + arg are stored
/// as <see cref="IntPtr"/> and the body runs in a static entry method (a
/// lambda can't capture a function pointer).</para>
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>Opaque thread handle — just an id into <see cref="_threads"/>.</summary>
    public struct thrd_t { public int Id; }
    /// <summary>Opaque mutex handle — just an id into <see cref="_mtx"/>.</summary>
    public struct mtx_t { public int Id; }

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

    private sealed class ThrdState
    {
        public IntPtr Func;   // (delegate*<void*,int>) stored as a pointer
        public IntPtr Arg;    // (void*)
        public int Result;
        public Thread? Thread;
    }

    private sealed class MtxState
    {
        public int Type;
        public readonly SemaphoreSlim Avail = new(1, 1);
        public int Owner;   // managed-thread id of the current holder (recursive bookkeeping)
        public int Count;   // recursion depth
    }

    private static readonly ConcurrentDictionary<int, ThrdState> _threads = new();
    private static readonly ConcurrentDictionary<int, MtxState> _mtx = new();
    private static int _nextThrdId;
    private static int _nextMtxId;

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
        var f = (delegate*<void*, int>)st.Func;
        st.Result = f((void*)st.Arg);
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
}
