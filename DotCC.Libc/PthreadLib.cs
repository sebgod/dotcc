#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DotCC.Libc;

/// <summary>
/// <c>&lt;pthread.h&gt;</c> and <c>&lt;sched.h&gt;</c>: POSIX threads on System.Threading, over the
/// tables <c>&lt;threads.h&gt;</c> keeps (see ThreadsLib.cs). A <c>pthread_mutex_t</c> and a
/// <c>pthread_cond_t</c> begin with the same <c>int</c> id an <c>mtx_t</c> and a <c>cnd_t</c> are,
/// so the C11 calls take them as they are; an id of 0 is a mutex or condition not yet made,
/// which its first use makes (the static initializers are zeros). The other fields are read by
/// the header's layouts: <c>pthread_mutex_t</c> { id, kind }, <c>pthread_cond_t</c> { id, clock },
/// <c>pthread_attr_t</c> { detachstate at 0, stacksize at 8, stackaddr at 16 }. Errors are
/// returned as error numbers, as POSIX has it, not through <c>errno</c>.
/// </summary>
public static unsafe partial class Libc
{
    private const int PTHREAD_CREATE_DETACHED = 1;
    private const int PTHREAD_MUTEX_RECURSIVE = 1, PTHREAD_MUTEX_ERRORCHECK = 2;
    private const int PTHREAD_STACK_MIN = 16384;

    /// <summary>A thread's stack when its attributes set none: glibc's 8 MiB.</summary>
    private const ulong PthreadDefaultStack = 8UL << 20;

    private sealed class PthreadState
    {
        public IntPtr Func;      // delegate*<void*, void*>
        public IntPtr Arg;
        public IntPtr Result;
        public Thread? Thread;
        public volatile bool Detached;
    }

    /// <summary>Unwinds a thread that called <see cref="pthread_exit"/> to its entry, which keeps
    /// <see cref="Value"/> as the thread's result.</summary>
    private sealed class PthreadExitException(IntPtr value) : Exception
    {
        public IntPtr Value { get; } = value;
    }

    /// <summary>The threads <see cref="pthread_create"/> made and that are not joined yet (or,
    /// detached, not finished yet), by id. The ids are <c>&lt;threads.h&gt;</c>'s, so
    /// <see cref="pthread_self"/> and <c>thrd_current</c> agree.</summary>
    private static readonly ConcurrentDictionary<int, PthreadState> _pthreads = new();

    /// <summary>True on a thread <see cref="pthread_create"/> made.</summary>
    [ThreadStatic] private static bool _isPthread;

    /// <summary><c>pthread_create(thread, attr, start, arg)</c>: run <paramref name="start"/>
    /// (<paramref name="arg"/>) on a new background thread, with the stack size and detach state
    /// of <paramref name="attr"/>, if any.</summary>
    public static int pthread_create(ulong* thread, void* attr, delegate*<void*, void*> start, void* arg)
    {
        if (thread == null || start == null) { return EINVAL; }
        var detached = attr != null && *(int*)attr == PTHREAD_CREATE_DETACHED;
        var stack = attr != null && *(ulong*)((byte*)attr + 8) is var s and > 0 ? s : PthreadDefaultStack;
        var id = Interlocked.Increment(ref _nextThrdId);
        var st = new PthreadState { Func = (IntPtr)start, Arg = (IntPtr)arg, Detached = detached };
        st.Thread = new Thread(PthreadEntry, (int)Math.Min(stack, int.MaxValue)) { IsBackground = true };
        _pthreads[id] = st;
        *thread = (ulong)id;
        st.Thread.Start(id);
        return 0;
    }

    /// <summary>A created thread's entry: it runs the start routine, keeps its result (or
    /// <see cref="pthread_exit"/>'s), runs the key destructors, and forgets a detached thread.</summary>
    private static void PthreadEntry(object? idObj)
    {
        var id = (int)idObj!;
        var st = _pthreads[id];
        _selfThrdId = id;
        _isPthread = true;
        try
        {
            st.Result = (IntPtr)((delegate*<void*, void*>)st.Func)((void*)st.Arg);
        }
        catch (PthreadExitException ex)
        {
            st.Result = ex.Value;
        }
        finally
        {
            RunTssDtors();
            if (st.Detached) { _pthreads.TryRemove(id, out _); }
        }
    }

    /// <summary><c>pthread_join(thread, retval)</c>: wait for <paramref name="thread"/> to end
    /// and take its result. EDEADLK for the calling thread itself, ESRCH for an unknown one,
    /// EINVAL for a detached one.</summary>
    public static int pthread_join(ulong thread, void** retval)
    {
        if ((int)thread == _selfThrdId) { return EDEADLK; }
        if (!_pthreads.TryGetValue((int)thread, out var st) || st.Thread is null) { return ESRCH; }
        if (st.Detached) { return EINVAL; }
        st.Thread.Join();
        if (retval != null) { *retval = (void*)st.Result; }
        _pthreads.TryRemove((int)thread, out _);
        return 0;
    }

    /// <summary><c>pthread_detach(thread)</c>: let <paramref name="thread"/> be forgotten when it
    /// ends (now, if it has).</summary>
    public static int pthread_detach(ulong thread)
    {
        if (!_pthreads.TryGetValue((int)thread, out var st)) { return ESRCH; }
        st.Detached = true;
        if (st.Thread is { IsAlive: false }) { _pthreads.TryRemove((int)thread, out _); }
        return 0;
    }

    /// <summary><c>pthread_self()</c>: the calling thread's id (one is given to a thread
    /// <see cref="pthread_create"/> did not make, the main one too, when it first asks).</summary>
    public static ulong pthread_self() => (ulong)thrd_current().Id;

    /// <summary><c>pthread_equal(a, b)</c>: non-zero when the ids name one thread.</summary>
    public static int pthread_equal(ulong a, ulong b) => a == b ? 1 : 0;

    /// <summary><c>pthread_exit(retval)</c>: end the calling thread with <paramref name="retval"/>
    /// as its result. On a thread <see cref="pthread_create"/> did not make (main), the process
    /// goes on until its other threads have ended, then exits with status 0, as POSIX has it.</summary>
    public static void pthread_exit(void* retval)
    {
        if (_isPthread) { throw new PthreadExitException((IntPtr)retval); }
        foreach (var st in _pthreads.Values) { st.Thread?.Join(); }
        exit(0);
    }

    /// <summary><c>pthread_once(once, init)</c>: <see cref="call_once"/>.</summary>
    public static int pthread_once(int* once, delegate*<void> init)
    {
        call_once(once, init);
        return 0;
    }

    // ---- thread attributes ------------------------------------------------

    /// <summary><c>pthread_attr_init(attr)</c>: joinable, default stack.</summary>
    public static int pthread_attr_init(void* attr)
    {
        *(int*)attr = 0;
        *(ulong*)((byte*)attr + 8) = 0;
        *(void**)((byte*)attr + 16) = null;
        return 0;
    }

    /// <summary><c>pthread_attr_destroy(attr)</c>: nothing to release.</summary>
    public static int pthread_attr_destroy(void* attr) => 0;

    /// <summary><c>pthread_attr_setdetachstate(attr, state)</c>: joinable (0) or detached (1).</summary>
    public static int pthread_attr_setdetachstate(void* attr, int state)
    {
        if (state is not (0 or PTHREAD_CREATE_DETACHED)) { return EINVAL; }
        *(int*)attr = state;
        return 0;
    }

    /// <summary><c>pthread_attr_getdetachstate(attr, state)</c>.</summary>
    public static int pthread_attr_getdetachstate(void* attr, int* state)
    {
        *state = *(int*)attr;
        return 0;
    }

    /// <summary><c>pthread_attr_setstacksize(attr, size)</c>: EINVAL below <c>PTHREAD_STACK_MIN</c>.</summary>
    public static int pthread_attr_setstacksize(void* attr, ulong size)
    {
        if (size < PTHREAD_STACK_MIN) { return EINVAL; }
        *(ulong*)((byte*)attr + 8) = size;
        return 0;
    }

    /// <summary><c>pthread_attr_getstacksize(attr, size)</c>: the size a thread made with
    /// <paramref name="attr"/> gets.</summary>
    public static int pthread_attr_getstacksize(void* attr, ulong* size)
    {
        var s = *(ulong*)((byte*)attr + 8);
        *size = s > 0 ? s : PthreadDefaultStack;
        return 0;
    }

    /// <summary><c>pthread_attr_getstack(attr, addr, size)</c>: no stack address is ever set (the
    /// runtime places a thread's stack), so the address is null.</summary>
    public static int pthread_attr_getstack(void* attr, void** addr, ulong* size)
    {
        *addr = *(void**)((byte*)attr + 16);
        return pthread_attr_getstacksize(attr, size);
    }

    // ---- mutexes ------------------------------------------------------------

    /// <summary><c>pthread_mutexattr_init(attr)</c>: a default (normal) mutex.</summary>
    public static int pthread_mutexattr_init(void* attr)
    {
        *(int*)attr = 0;
        return 0;
    }

    /// <summary><c>pthread_mutexattr_destroy(attr)</c>: nothing to release.</summary>
    public static int pthread_mutexattr_destroy(void* attr) => 0;

    /// <summary><c>pthread_mutexattr_settype(attr, kind)</c>: normal, recursive or error-checking.</summary>
    public static int pthread_mutexattr_settype(void* attr, int kind)
    {
        if (kind is < 0 or > PTHREAD_MUTEX_ERRORCHECK) { return EINVAL; }
        *(int*)attr = kind;
        return 0;
    }

    /// <summary><c>pthread_mutexattr_gettype(attr, kind)</c>.</summary>
    public static int pthread_mutexattr_gettype(void* attr, int* kind)
    {
        *kind = *(int*)attr;
        return 0;
    }

    /// <summary><c>pthread_mutex_init(mutex, attr)</c>: free to take, of <paramref name="attr"/>'s
    /// kind; made on its first use, as a statically initialized one is.</summary>
    public static int pthread_mutex_init(void* mutex, void* attr)
    {
        ((int*)mutex)[0] = 0;
        ((int*)mutex)[1] = attr != null ? *(int*)attr : 0;
        return 0;
    }

    /// <summary>The state of <paramref name="mutex"/>, made now if it is not yet: whichever
    /// thread publishes its id first wins, and a loser's state is dropped.</summary>
    private static MtxState PthreadMutex(void* mutex)
    {
        var slot = (int*)mutex;
        var id = Volatile.Read(ref *slot);
        if (id != 0 && _mtx.TryGetValue(id, out var existing)) { return existing; }
        var fresh = new MtxState { Type = slot[1] == PTHREAD_MUTEX_RECURSIVE ? mtx_recursive : mtx_plain };
        var newId = Interlocked.Increment(ref _nextMtxId);
        _mtx[newId] = fresh;
        var seen = Interlocked.CompareExchange(ref *slot, newId, id);
        if (seen == id) { return fresh; }
        _mtx.TryRemove(newId, out _);
        fresh.Avail.Dispose();
        return _mtx[seen];
    }

    /// <summary><c>pthread_mutex_destroy(mutex)</c>: EBUSY while it is locked.</summary>
    public static int pthread_mutex_destroy(void* mutex)
    {
        var id = *(int*)mutex;
        if (id == 0) { return 0; }
        if (_mtx.TryGetValue(id, out var st) && st.Avail.CurrentCount == 0) { return EBUSY; }
        mtx_destroy((mtx_t*)mutex);
        *(int*)mutex = 0;
        return 0;
    }

    /// <summary><c>pthread_mutex_lock(mutex)</c>: EDEADLK when an error-checking mutex is the
    /// caller's already (a normal one deadlocks, as POSIX says it does).</summary>
    public static int pthread_mutex_lock(void* mutex)
    {
        var st = PthreadMutex(mutex);
        if (((int*)mutex)[1] == PTHREAD_MUTEX_ERRORCHECK && st.Owner == Environment.CurrentManagedThreadId) { return EDEADLK; }
        mtx_lock((mtx_t*)mutex);
        return 0;
    }

    /// <summary><c>pthread_mutex_trylock(mutex)</c>: EBUSY when another thread holds it (or, not
    /// recursive, this one).</summary>
    public static int pthread_mutex_trylock(void* mutex)
    {
        PthreadMutex(mutex);
        return mtx_trylock((mtx_t*)mutex) == thrd_success ? 0 : EBUSY;
    }

    /// <summary><c>pthread_mutex_timedlock(mutex, abstime)</c>: ETIMEDOUT once the
    /// CLOCK_REALTIME deadline passes.</summary>
    public static int pthread_mutex_timedlock(void* mutex, timespec* abstime)
    {
        var st = PthreadMutex(mutex);
        if (((int*)mutex)[1] == PTHREAD_MUTEX_ERRORCHECK && st.Owner == Environment.CurrentManagedThreadId) { return EDEADLK; }
        return mtx_timedlock((mtx_t*)mutex, abstime) == thrd_success ? 0 : ETIMEDOUT;
    }

    /// <summary><c>pthread_mutex_unlock(mutex)</c>: EPERM when the caller does not hold it.</summary>
    public static int pthread_mutex_unlock(void* mutex)
    {
        var st = PthreadMutex(mutex);
        if (st.Avail.CurrentCount != 0 || st.Owner != Environment.CurrentManagedThreadId) { return EPERM; }
        mtx_unlock((mtx_t*)mutex);
        return 0;
    }

    // ---- condition variables ----------------------------------------------

    /// <summary><c>pthread_condattr_init(attr)</c>: deadlines on CLOCK_REALTIME.</summary>
    public static int pthread_condattr_init(void* attr)
    {
        *(int*)attr = 0;
        return 0;
    }

    /// <summary><c>pthread_condattr_destroy(attr)</c>: nothing to release.</summary>
    public static int pthread_condattr_destroy(void* attr) => 0;

    /// <summary><c>pthread_condattr_setclock(attr, clock)</c>: CLOCK_REALTIME (0) or
    /// CLOCK_MONOTONIC (1); EINVAL for a CPU-time clock.</summary>
    public static int pthread_condattr_setclock(void* attr, int clock)
    {
        if (clock is not (0 or 1)) { return EINVAL; }
        *(int*)attr = clock;
        return 0;
    }

    /// <summary><c>pthread_condattr_getclock(attr, clock)</c>.</summary>
    public static int pthread_condattr_getclock(void* attr, int* clock)
    {
        *clock = *(int*)attr;
        return 0;
    }

    /// <summary><c>pthread_cond_init(cond, attr)</c>: with <paramref name="attr"/>'s clock; made
    /// on its first use, as a statically initialized one is.</summary>
    public static int pthread_cond_init(void* cond, void* attr)
    {
        ((int*)cond)[0] = 0;
        ((int*)cond)[1] = attr != null ? *(int*)attr : 0;
        return 0;
    }

    /// <summary>The state of <paramref name="cond"/>, made now if it is not yet (see
    /// <see cref="PthreadMutex"/>).</summary>
    private static CndState PthreadCond(void* cond)
    {
        var slot = (int*)cond;
        var id = Volatile.Read(ref *slot);
        if (id != 0 && _cnd.TryGetValue(id, out var existing)) { return existing; }
        var fresh = new CndState();
        var newId = Interlocked.Increment(ref _nextCndId);
        _cnd[newId] = fresh;
        var seen = Interlocked.CompareExchange(ref *slot, newId, id);
        if (seen == id) { return fresh; }
        _cnd.TryRemove(newId, out _);
        return _cnd[seen];
    }

    /// <summary><c>pthread_cond_destroy(cond)</c>.</summary>
    public static int pthread_cond_destroy(void* cond)
    {
        if (*(int*)cond != 0) { cnd_destroy((cnd_t*)cond); }
        *(int*)cond = 0;
        return 0;
    }

    /// <summary><c>pthread_cond_signal(cond)</c>: wake one waiter.</summary>
    public static int pthread_cond_signal(void* cond)
    {
        PthreadCond(cond);
        cnd_signal((cnd_t*)cond);
        return 0;
    }

    /// <summary><c>pthread_cond_broadcast(cond)</c>: wake every waiter.</summary>
    public static int pthread_cond_broadcast(void* cond)
    {
        PthreadCond(cond);
        cnd_broadcast((cnd_t*)cond);
        return 0;
    }

    /// <summary><c>pthread_cond_wait(cond, mutex)</c>: EPERM unless the caller holds
    /// <paramref name="mutex"/>.</summary>
    public static int pthread_cond_wait(void* cond, void* mutex)
    {
        PthreadCond(cond);
        var m = PthreadMutex(mutex);
        if (m.Owner != Environment.CurrentManagedThreadId) { return EPERM; }
        cnd_wait((cnd_t*)cond, (mtx_t*)mutex);
        return 0;
    }

    /// <summary><c>pthread_cond_timedwait(cond, mutex, abstime)</c>: as
    /// <see cref="pthread_cond_wait"/>, giving up with ETIMEDOUT once the deadline, read on the
    /// condition's clock, passes; the mutex is held again either way.</summary>
    public static int pthread_cond_timedwait(void* cond, void* mutex, timespec* abstime)
    {
        var c = PthreadCond(cond);
        var m = PthreadMutex(mutex);
        if (m.Owner != Environment.CurrentManagedThreadId) { return EPERM; }
        if (abstime == null || abstime->tv_nsec is < 0 or >= 1_000_000_000) { return EINVAL; }
        timespec now;
        clock_gettime(((int*)cond)[1], &now);
        var left = (Int128)(abstime->tv_sec - now.tv_sec) * 1_000_000_000 + (abstime->tv_nsec - now.tv_nsec);
        if (left <= 0) { return ETIMEDOUT; }
        var ms = (int)Int128.Min((left + 999_999) / 1_000_000, int.MaxValue);
        Monitor.Enter(c.Lock);
        mtx_unlock((mtx_t*)mutex);
        var signalled = Monitor.Wait(c.Lock, ms);
        Monitor.Exit(c.Lock);
        mtx_lock((mtx_t*)mutex);
        return signalled ? 0 : ETIMEDOUT;
    }

    // ---- thread-specific data -------------------------------------------

    /// <summary><c>pthread_key_create(key, destructor)</c>: <see cref="tss_create"/>.</summary>
    public static int pthread_key_create(uint* key, delegate*<void*, void> destructor)
    {
        tss_t t;
        if (tss_create(&t, destructor) != thrd_success) { return EAGAIN; }
        *key = (uint)t.Id;
        return 0;
    }

    /// <summary><c>pthread_key_delete(key)</c>: <see cref="tss_delete"/>.</summary>
    public static int pthread_key_delete(uint key)
    {
        tss_delete(new tss_t { Id = (int)key });
        return 0;
    }

    /// <summary><c>pthread_getspecific(key)</c>: the calling thread's value, null if unset.</summary>
    public static void* pthread_getspecific(uint key) => tss_get(new tss_t { Id = (int)key });

    /// <summary><c>pthread_setspecific(key, value)</c>: set the calling thread's value.</summary>
    public static int pthread_setspecific(uint key, void* value)
    {
        tss_set(new tss_t { Id = (int)key }, value);
        return 0;
    }

    // ---- <sched.h> -----------------------------------------------------------

    /// <summary><c>sched_yield()</c>: give up the rest of the time slice.</summary>
    public static int sched_yield()
    {
        Thread.Yield();
        return 0;
    }
}
