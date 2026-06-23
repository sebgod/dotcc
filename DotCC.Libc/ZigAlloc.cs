#nullable enable

namespace DotCC.Libc;

/// <summary>
/// The runtime backing for the Zig front-end's allocators (Milestone F) — the shared
/// <c>std.mem.Allocator</c> abstraction. Auto-spliced into every emitted program (the
/// <c>DotCC.Libc/*.cs</c> <c>&lt;EmbeddedResource&gt;</c> glob) and compiled into
/// <c>DotCC.Libc.dll</c> for the unit tests, exactly like <see cref="Slice{T}"/> /
/// <see cref="ErrUnion{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Zig's <c>std.mem.Allocator</c> is a fat pointer <c>{ ptr, *vtable }</c> whose high-level
/// generic methods (<c>alloc</c>/<c>free</c>) dispatch through a vtable of raw byte-level
/// function pointers. dotcc mirrors that shape with <see cref="Allocator"/> (the fat pointer),
/// <see cref="AllocatorVTable"/> (the fn-ptr table), and a second concrete allocator,
/// <see cref="FixedBufferAllocator"/>, to exercise genuine indirect dispatch.
/// </para>
/// <para>
/// <b>Devirtualization.</b> When the lowering can PROVE the allocator is the statically-known
/// C-heap default (<c>std.heap.page_allocator</c> / <c>std.heap.c_allocator</c>), it bypasses
/// the vtable entirely and emits a direct call to <see cref="AllocCHeap{T}"/> /
/// <see cref="FreeCHeap{T}"/> (which reach <see cref="Libc.malloc"/>/<see cref="Libc.free"/>
/// with no indirection). A runtime-selected allocator (an opaque <c>std.mem.Allocator</c>
/// parameter, or a <see cref="FixedBufferAllocator"/>'s <see cref="FbaAllocator"/> result) pays
/// the indirect <c>delegate*</c> dispatch through <see cref="Allocator.Alloc{T}"/> /
/// <see cref="Allocator.Free{T}"/> instead. Both paths produce the same
/// <c>ErrUnion&lt;Slice&lt;T&gt;&gt;</c>, so they compose with <c>try</c>/<c>catch</c>.
/// </para>
/// <para>
/// The vtable function pointers are MANAGED (<c>delegate*&lt;…&gt;</c>, not
/// <c>unmanaged[Cdecl]</c>): an <see cref="Allocator"/> never crosses a real C ABI (it is a
/// Zig-internal abstraction), and a managed function pointer targets a plain <c>static</c>
/// method with no <c>[UnmanagedCallersOnly]</c> ceremony, which keeps the whole file
/// AOT-clean. The vtable is carried <b>by value</b> inside <see cref="Allocator"/> (rather
/// than a <c>*const VTable</c>) so there is no managed-static address to pin — the fn-ptr
/// targets are themselves address-stable, so the table is rebuilt cheaply at each
/// materialization point.
/// </para>
/// </remarks>
public unsafe struct AllocatorVTable
{
    /// <summary>Raw allocation: <c>(ctx, byteLen) → byte*</c>; null on failure.</summary>
    public delegate*<void*, nuint, byte*> AllocFn;

    /// <summary>Raw free: <c>(ctx, ptr, byteLen) → void</c>.</summary>
    public delegate*<void*, byte*, nuint, void> FreeFn;
}

/// <summary>
/// The Zig <c>std.mem.Allocator</c> fat pointer — an opaque context <see cref="Ctx"/> plus a
/// vtable of raw byte-level allocation function pointers. The high-level generic
/// <see cref="Alloc{T}"/> / <see cref="Free{T}"/> compute element sizes and wrap the raw
/// results in a <see cref="Slice{T}"/> / <see cref="ErrUnion{T}"/>, dispatching through the
/// vtable (the indirect path; the devirt path on <see cref="ZigAlloc"/> bypasses this).
/// </summary>
public unsafe struct Allocator
{
    /// <summary>The opaque allocator context (e.g. a <c>FixedBufferAllocator*</c>; null for
    /// the stateless C heap).</summary>
    public void* Ctx;

    /// <summary>The raw allocation function pointers (carried by value — see the file remarks).</summary>
    public AllocatorVTable Vtable;

    /// <summary><c>a.alloc(T, n)</c> — allocate <paramref name="n"/> elements of
    /// <typeparamref name="T"/> through the vtable. Returns a <see cref="Slice{T}"/> on
    /// success, or the error code <paramref name="oom"/> (Zig's <c>error.OutOfMemory</c>) when
    /// the raw allocation returns null. The genuine indirect dispatch lives here.</summary>
    public ErrUnion<Slice<T>> Alloc<T>(ulong n, ushort oom) where T : unmanaged
    {
        nuint bytes = (nuint)n * (nuint)sizeof(T);
        byte* p = Vtable.AllocFn(Ctx, bytes);
        return p == null
            ? ErrUnion<Slice<T>>.Err(oom)
            : ErrUnion<Slice<T>>.Ok(new Slice<T>((T*)p, n));
    }

    /// <summary><c>a.free(slice)</c> — free a slice previously returned by
    /// <see cref="Alloc{T}"/>, passing the byte length back to the raw free (Zig allocators
    /// take the size at free time).</summary>
    public void Free<T>(Slice<T> s) where T : unmanaged
        => Vtable.FreeFn(Ctx, (byte*)s.Ptr, (nuint)s.Len * (nuint)sizeof(T));

    /// <summary><c>a.create(T)</c> — allocate one <typeparamref name="T"/> through the vtable
    /// (Milestone U). The address is carried back as a <c>nuint</c>: a pointer cannot be the
    /// generic argument of <see cref="ErrUnion{T}"/> (<c>T : unmanaged</c> excludes pointer
    /// types), so <c>Error!*T</c> is represented as <c>ErrUnion&lt;nuint&gt;</c> and the caller
    /// casts the unwrapped value back to <c>T*</c>. Returns <paramref name="oom"/> on failure.</summary>
    public ErrUnion<nuint> Create<T>(ushort oom) where T : unmanaged
    {
        byte* p = Vtable.AllocFn(Ctx, (nuint)sizeof(T));
        return p == null ? ErrUnion<nuint>.Err(oom) : ErrUnion<nuint>.Ok((nuint)p);
    }

    /// <summary><c>a.destroy(p)</c> — free a single object previously returned by
    /// <see cref="Create{T}"/>, passing <c>sizeof(T)</c> back as the byte length. A <c>T*</c>
    /// is a legal method PARAMETER even though it cannot be a generic type ARGUMENT.</summary>
    public void Destroy<T>(T* p) where T : unmanaged
        => Vtable.FreeFn(Ctx, (byte*)p, (nuint)sizeof(T));
}

/// <summary>
/// A bump allocator over a caller-supplied fixed byte buffer — Zig's
/// <c>std.heap.FixedBufferAllocator</c>. The second concrete allocator, so the
/// <see cref="Allocator"/> vtable has something other than the C heap to dispatch to. No heap,
/// fully deterministic: allocation past the buffer end returns null (→ <c>error.OutOfMemory</c>),
/// which makes overflow an observable, repeatable test.
/// </summary>
public unsafe struct FixedBufferAllocator
{
    /// <summary>The backing buffer.</summary>
    public byte* Buffer;

    /// <summary>The buffer's capacity in bytes.</summary>
    public ulong Capacity;

    /// <summary>The bump cursor — bytes handed out so far.</summary>
    public ulong EndIndex;

    /// <summary><c>FixedBufferAllocator.init(buf)</c> — wrap a buffer of
    /// <paramref name="cap"/> bytes at <paramref name="buf"/>.</summary>
    public static FixedBufferAllocator Init(byte* buf, ulong cap)
        => new() { Buffer = buf, Capacity = cap, EndIndex = 0 };
}

/// <summary>
/// Static entry points for the Zig allocator runtime: the C-heap raw functions (which back the
/// devirtualized default), the <see cref="FixedBufferAllocator"/> raw functions, and the
/// factory helpers the lowering emits (<see cref="CHeap"/>, <see cref="FbaAllocator"/>, and the
/// devirt'd <see cref="AllocCHeap{T}"/> / <see cref="FreeCHeap{T}"/>).
/// </summary>
public static unsafe class ZigAlloc
{
    // ---- C heap (the statically-known default) ---------------------------

    /// <summary>Raw C-heap allocation — the vtable's <c>AllocFn</c> for a materialized default
    /// allocator. <see cref="Libc.malloc"/> takes an <c>int</c>, so a request wider than
    /// <see cref="int.MaxValue"/> is treated as a failure (null).</summary>
    private static byte* CHeapAlloc(void* ctx, nuint len)
        => len > int.MaxValue ? null : (byte*)Libc.malloc((int)len);

    /// <summary>Raw C-heap free — the vtable's <c>FreeFn</c>. <see cref="Libc.free"/> ignores
    /// the size, so it is dropped.</summary>
    private static void CHeapFree(void* ctx, byte* p, nuint len) => Libc.free(p);

    /// <summary>Materialize the C-heap default as a runtime <see cref="Allocator"/> value —
    /// used when the statically-known default is passed to an opaque
    /// <c>std.mem.Allocator</c> sink (a parameter / return), where it must become a real
    /// fat-pointer value. Its vtable still reaches <see cref="Libc.malloc"/>/<see cref="Libc.free"/>.</summary>
    public static Allocator CHeap()
        => new() { Ctx = null, Vtable = new AllocatorVTable { AllocFn = &CHeapAlloc, FreeFn = &CHeapFree } };

    /// <summary>The <b>devirtualized</b> <c>page_allocator.alloc(T, n)</c> — a direct
    /// <see cref="Libc.malloc"/>, no vtable load. Emitted whenever the lowering proves the
    /// allocator is the statically-known C-heap default.</summary>
    public static ErrUnion<Slice<T>> AllocCHeap<T>(ulong n, ushort oom) where T : unmanaged
    {
        nuint bytes = (nuint)n * (nuint)sizeof(T);
        byte* p = CHeapAlloc(null, bytes);
        return p == null
            ? ErrUnion<Slice<T>>.Err(oom)
            : ErrUnion<Slice<T>>.Ok(new Slice<T>((T*)p, n));
    }

    /// <summary>The <b>devirtualized</b> <c>page_allocator.free(slice)</c> — a direct
    /// <see cref="Libc.free"/>.</summary>
    public static void FreeCHeap<T>(Slice<T> s) where T : unmanaged
        => CHeapFree(null, (byte*)s.Ptr, 0);

    /// <summary>The <b>devirtualized</b> <c>page_allocator.create(T)</c> — a direct
    /// <see cref="Libc.malloc"/> of <c>sizeof(T)</c> bytes, no vtable. The address is carried as a
    /// <c>nuint</c> (see <see cref="Allocator.Create{T}"/> for why).</summary>
    public static ErrUnion<nuint> CreateCHeap<T>(ushort oom) where T : unmanaged
    {
        byte* p = CHeapAlloc(null, (nuint)sizeof(T));
        return p == null ? ErrUnion<nuint>.Err(oom) : ErrUnion<nuint>.Ok((nuint)p);
    }

    /// <summary>The <b>devirtualized</b> <c>page_allocator.destroy(p)</c> — a direct
    /// <see cref="Libc.free"/>.</summary>
    public static void DestroyCHeap<T>(T* p) where T : unmanaged => CHeapFree(null, (byte*)p, 0);

    // ---- FixedBufferAllocator (the second allocator) ---------------------

    /// <summary>Raw FBA allocation — bump <see cref="FixedBufferAllocator.EndIndex"/>; return
    /// null when the request would overflow the buffer (Zig's <c>error.OutOfMemory</c>).</summary>
    private static byte* FbaAlloc(void* ctx, nuint len)
    {
        var self = (FixedBufferAllocator*)ctx;
        ulong end = self->EndIndex + (ulong)len;
        if (end > self->Capacity) { return null; }
        byte* p = self->Buffer + self->EndIndex;
        self->EndIndex = end;
        return p;
    }

    /// <summary>Raw FBA free — a no-op. A bump allocator only reclaims by resetting the whole
    /// arena; per-slice free is a no-op (matches Zig's FBA for any but the last allocation, and
    /// is observationally fine for dotcc's exit-code oracle).</summary>
    private static void FbaFree(void* ctx, byte* p, nuint len) { }

    /// <summary><c>fba.allocator()</c> — produce an <see cref="Allocator"/> fat pointer whose
    /// context is the FBA and whose vtable bumps it. The caller passes <c>&amp;fba</c>, so the
    /// returned allocator is valid only while that <see cref="FixedBufferAllocator"/> local is
    /// alive (the same stack-lifetime rule as Zig).</summary>
    public static Allocator FbaAllocator(FixedBufferAllocator* self)
        => new() { Ctx = self, Vtable = new AllocatorVTable { AllocFn = &FbaAlloc, FreeFn = &FbaFree } };

    // ---- array-by-value return (the Milestone K cut, made sound) ---------

    /// <summary>Copy an <paramref name="n"/>-element array at <paramref name="src"/> into a fresh
    /// heap-owned buffer and return the pointer — the sound lowering of a Zig <c>[N]T</c> by-value
    /// return. The callee's array local is a <c>stackalloc</c> that dies with its frame, but Zig
    /// arrays are VALUE types, so the caller must receive a copy that outlives the call (returning
    /// the stackalloc pointer directly would be a dangling pointer). V1: the buffer is NOT freed
    /// (leaked — sound values, unfreed); a caller-allocated result pointer (sret) would avoid the
    /// leak but rewrites the call ABI. BCL names are fully qualified because the runtime-splice
    /// strips <c>using</c> directives when this file is embedded into an emitted program.</summary>
    public static T* CopyArrayResult<T>(T* src, int n) where T : unmanaged
    {
        nuint bytes = (nuint)n * (nuint)sizeof(T);
        var dst = (T*)System.Runtime.InteropServices.NativeMemory.Alloc(bytes);
        System.Buffer.MemoryCopy(src, dst, (long)bytes, (long)bytes);
        return dst;
    }
}
