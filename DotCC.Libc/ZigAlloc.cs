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
    /// <summary>Raw allocation: <c>(ctx, len, alignment, ret_addr) → ?[*]u8</c>; null on failure.
    /// Mirrors Zig's <c>VTable.alloc</c> exactly (the field name matches, so a user
    /// <c>std.mem.Allocator.VTable{ .alloc = … }</c> literal binds 1:1).</summary>
    public delegate*<void*, ulong, Alignment, ulong, byte*> alloc;

    /// <summary>In-place resize: <c>(ctx, memory: []u8, alignment, new_len, ret_addr) → bool</c>.
    /// Stored for shape-fidelity (so a custom vtable matches real zig); dotcc's own <c>realloc</c>
    /// emulates via alloc+copy+free, and <c>a.resize</c>/<c>a.remap</c> are deferred, so this is
    /// never invoked by dotcc-lowered code today.</summary>
    public delegate*<void*, Slice<byte>, Alignment, ulong, ulong, CBool> resize;

    /// <summary>Resize-possibly-moving: <c>(ctx, memory: []u8, alignment, new_len, ret_addr) → ?[*]u8</c>.
    /// Stored for shape-fidelity; not invoked by dotcc dispatch (see <see cref="resize"/>).</summary>
    public delegate*<void*, Slice<byte>, Alignment, ulong, ulong, byte*> remap;

    /// <summary>Raw free: <c>(ctx, memory: []u8, alignment, ret_addr) → void</c>.</summary>
    public delegate*<void*, Slice<byte>, Alignment, ulong, void> free;
}

/// <summary>
/// Models Zig's <c>std.mem.Alignment</c> (Milestone W) — a power-of-two byte alignment threaded
/// through the allocator vtable. Real zig stores <c>log2(bytes)</c> in an <c>enum(u6)</c>; dotcc
/// carries the byte count directly and exposes <see cref="toByteUnits"/> (identity). The
/// <see cref="FixedBufferAllocator"/> genuinely honors the request (it aligns its bump pointer up);
/// the C heap and arena over-align (≥16-aligned) and dotcc's <see cref="ZigAlloc.AlignOf{T}"/> caps
/// requests at 16, so on those two the exact value is satisfied by construction. <b>Cut:</b>
/// <c>@intFromEnum</c> (the log2) is not modeled — use <c>.toByteUnits()</c>.
/// </summary>
public readonly struct Alignment
{
    private readonly ulong _bytes;

    /// <summary>Construct an alignment of <paramref name="bytes"/> bytes (a power of two).</summary>
    public Alignment(ulong bytes) { _bytes = bytes; }

    /// <summary>Zig's <c>Alignment.toByteUnits()</c> — the alignment in bytes.</summary>
    public ulong toByteUnits() => _bytes;
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
        ulong bytes = n * (ulong)sizeof(T);
        byte* p = Vtable.alloc(Ctx, bytes, AlignOf<T>(), 0);
        return p == null
            ? ErrUnion<Slice<T>>.Err(oom)
            : ErrUnion<Slice<T>>.Ok(new Slice<T>((T*)p, n));
    }

    /// <summary><c>a.free(slice)</c> — free a slice previously returned by
    /// <see cref="Alloc{T}"/>, passing the byte-length <c>[]u8</c> back to the raw free (Zig
    /// allocators take the size at free time).</summary>
    public void Free<T>(Slice<T> s) where T : unmanaged
        => Vtable.free(Ctx, new Slice<byte>((byte*)s.Ptr, s.Len * (ulong)sizeof(T)), AlignOf<T>(), 0);

    /// <summary>The byte alignment dotcc passes for an element type <typeparamref name="T"/>.
    /// Delegates to <see cref="ZigAlloc.AlignOf{T}"/> — the single source of truth shared with the
    /// devirtualized allocator paths — so the vtable and devirt paths request identical alignment.</summary>
    private static Alignment AlignOf<T>() where T : unmanaged => ZigAlloc.AlignOf<T>();

    /// <summary><c>a.create(T)</c> — allocate one <typeparamref name="T"/> through the vtable
    /// (Milestone U). The address is carried back as a <c>nuint</c>: a pointer cannot be the
    /// generic argument of <see cref="ErrUnion{T}"/> (<c>T : unmanaged</c> excludes pointer
    /// types), so <c>Error!*T</c> is represented as <c>ErrUnion&lt;nuint&gt;</c> and the caller
    /// casts the unwrapped value back to <c>T*</c>. Returns <paramref name="oom"/> on failure.</summary>
    public ErrUnion<nuint> Create<T>(ushort oom) where T : unmanaged
    {
        byte* p = Vtable.alloc(Ctx, (ulong)sizeof(T), AlignOf<T>(), 0);
        return p == null ? ErrUnion<nuint>.Err(oom) : ErrUnion<nuint>.Ok((nuint)p);
    }

    /// <summary><c>a.destroy(p)</c> — free a single object previously returned by
    /// <see cref="Create{T}"/>, passing <c>sizeof(T)</c> back as the byte length. A <c>T*</c>
    /// is a legal method PARAMETER even though it cannot be a generic type ARGUMENT.</summary>
    public void Destroy<T>(T* p) where T : unmanaged
        => Vtable.free(Ctx, new Slice<byte>((byte*)p, (ulong)sizeof(T)), AlignOf<T>(), 0);

    /// <summary><c>a.realloc(slice, n)</c> (Milestone U) — grow/shrink through the vtable. The
    /// <see cref="Allocator"/> vtable has only alloc/free, so realloc is EMULATED: allocate a fresh
    /// region, copy the preserved prefix, free the old. Returns the new <see cref="Slice{T}"/> or
    /// <paramref name="oom"/>.</summary>
    public ErrUnion<Slice<T>> Realloc<T>(Slice<T> old, ulong n, ushort oom) where T : unmanaged
    {
        var fresh = Alloc<T>(n, oom);
        if (fresh.IsErr) { return fresh; }
        ulong keep = old.Len < n ? old.Len : n;
        System.Buffer.MemoryCopy(old.Ptr, fresh.Value.Ptr, (long)(n * (ulong)sizeof(T)), (long)(keep * (ulong)sizeof(T)));
        Free(old);
        return fresh;
    }

    /// <summary><c>a.resize(slice, n)</c> — attempt an IN-PLACE resize (no move) through the vtable's
    /// <see cref="AllocatorVTable.resize"/> fn, returning whether it succeeded (Zig's <c>bool</c>).
    /// The answer is whatever the concrete runtime allocator gives: an <see cref="FixedBufferAllocator"/>
    /// applies its last-allocation logic (grow the top block if the buffer has room, shrink any block),
    /// while the C heap and arena always report <c>false</c> (they can't grow/shrink in place, so the
    /// caller falls back to a copying realloc). No <c>ret_addr</c> is threaded (0), matching the other
    /// dispatch helpers.</summary>
    public CBool Resize<T>(Slice<T> s, ulong n) where T : unmanaged
        => Vtable.resize(Ctx, new Slice<byte>((byte*)s.Ptr, s.Len * (ulong)sizeof(T)), AlignOf<T>(), n * (ulong)sizeof(T), 0);

    /// <summary><c>a.remap(slice, n)</c> — resize-possibly-moving through the vtable's
    /// <see cref="AllocatorVTable.remap"/> fn. Returns the (possibly relocated) slice of
    /// <paramref name="n"/> elements on success, or <c>null</c> (Zig's <c>?[]T</c>) when the
    /// allocator can't honor it. dotcc's allocators never actually move a block (an FBA remaps in
    /// place; the C heap / arena return null), so a non-null result carries the same pointer with the
    /// new length — but the shape follows real zig, which is free to relocate.</summary>
    public Slice<T>? Remap<T>(Slice<T> s, ulong n) where T : unmanaged
    {
        byte* p = Vtable.remap(Ctx, new Slice<byte>((byte*)s.Ptr, s.Len * (ulong)sizeof(T)), AlignOf<T>(), n * (ulong)sizeof(T), 0);
        return p == null ? null : new Slice<T>((T*)p, n);
    }
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

/// <summary>The header of one <see cref="ArenaAllocator"/> chunk — a singly-linked node prefixing a
/// run of <see cref="Cap"/> bump-allocatable bytes. The usable space starts a fixed,
/// 16-byte-aligned distance after the header (<c>ZigAlloc.ArenaHeaderBytes</c>), so every handed-out
/// pointer is at least 16-aligned. Public so it can be the type of <see cref="ArenaAllocator"/>'s
/// public <c>Current</c> field (a self-pointer chain has no encapsulation to protect here).</summary>
public unsafe struct ArenaChunk
{
    /// <summary>The previously-allocated chunk (freed before this one at deinit), or null.</summary>
    public ArenaChunk* Prev;

    /// <summary>The usable capacity in bytes (after the aligned header).</summary>
    public nuint Cap;

    /// <summary>Bytes handed out of this chunk so far.</summary>
    public nuint Used;
}

/// <summary>
/// A growing arena over a backing <see cref="Allocator"/> — Zig's <c>std.heap.ArenaAllocator</c>.
/// The third concrete allocator: it bump-allocates within malloc'd chunks (grown on demand from
/// <see cref="Backing"/>) and reclaims EVERYTHING at once in <see cref="Deinit"/> — the headline
/// pairing with <c>defer arena.deinit()</c>. Per-allocation <c>free</c> is a no-op (an arena only
/// frees wholesale). AOT-clean: a raw <see cref="ArenaChunk"/>* chain, no managed collections.
/// </summary>
public unsafe struct ArenaAllocator
{
    /// <summary>The backing allocator the chunks are drawn from (and returned to at deinit).</summary>
    public Allocator Backing;

    /// <summary>The current (newest) chunk — null until the first allocation.</summary>
    public ArenaChunk* Current;

    /// <summary><c>std.heap.ArenaAllocator.init(backing)</c> — wrap a backing allocator. No chunk is
    /// allocated until the first <c>alloc</c>.</summary>
    public static ArenaAllocator Init(Allocator backing)
        => new() { Backing = backing, Current = null };

    /// <summary><c>arena.deinit()</c> — free every chunk back to <see cref="Backing"/> (newest
    /// first) and reset. Idempotent: a second call sees an empty chain.</summary>
    public void Deinit()
    {
        ArenaChunk* ch = Current;
        while (ch != null)
        {
            ArenaChunk* prev = ch->Prev;
            Backing.Vtable.free(Backing.Ctx,
                new Slice<byte>((byte*)ch, (ulong)((nuint)ZigAlloc.ArenaHeaderBytes + ch->Cap)), new Alignment(16), 0);
            ch = prev;
        }
        Current = null;
    }
}

/// <summary>
/// Static entry points for the Zig allocator runtime: the C-heap raw functions (which back the
/// devirtualized default), the <see cref="FixedBufferAllocator"/> raw functions, and the
/// factory helpers the lowering emits (<see cref="CHeap"/>, <see cref="FbaAllocator"/>, and the
/// devirt'd <see cref="AllocCHeap{T}"/> / <see cref="FreeCHeap{T}"/>).
/// </summary>
public static unsafe class ZigAlloc
{
    /// <summary>The byte alignment dotcc requests for an element type <typeparamref name="T"/> — the
    /// largest power of two ≤ <c>min(sizeof(T), 16)</c>. The single source of truth: both the vtable
    /// path (<see cref="Allocator"/>) and the devirtualized allocator sites feed it, so a given
    /// source allocation requests the same alignment either way. .NET has no <c>alignof</c>, so this
    /// size-derived value is an approximation — exact alignment is not modeled (documented in
    /// <see cref="Alignment"/>). It caps at 16, which is why the C heap (≥16-aligned) and the arena
    /// (16-aligned data start) satisfy every request dotcc can generate.</summary>
    internal static Alignment AlignOf<T>() where T : unmanaged
    {
        ulong s = (ulong)sizeof(T);
        ulong a = 1;
        while ((a << 1) <= s && (a << 1) <= 16) { a <<= 1; }
        return new Alignment(a);
    }

    // ---- C heap (the statically-known default) ---------------------------

    /// <summary>Raw C-heap allocation — the vtable's <c>alloc</c> for a materialized default
    /// allocator. <see cref="Libc.malloc"/> takes an <c>int</c>, so a request wider than
    /// <see cref="int.MaxValue"/> is treated as a failure (null).</summary>
    private static byte* CHeapAlloc(void* ctx, ulong len, Alignment alignment, ulong retAddr)
        => len > int.MaxValue ? null : (byte*)Libc.malloc((int)len);

    /// <summary>Raw C-heap resize — always <c>false</c> (malloc can't grow/shrink in place; the
    /// caller falls back to a copying realloc). Part of the 4-fn vtable shape; see
    /// <see cref="AllocatorVTable.resize"/>.</summary>
    private static CBool CHeapResize(void* ctx, Slice<byte> memory, Alignment alignment, ulong newLen, ulong retAddr) => false;

    /// <summary>Raw C-heap remap — always <c>null</c> (no in-place move; the caller copies).</summary>
    private static byte* CHeapRemap(void* ctx, Slice<byte> memory, Alignment alignment, ulong newLen, ulong retAddr) => null;

    /// <summary>Raw C-heap free — the vtable's <c>free</c>. <see cref="Libc.free"/> ignores the
    /// size + alignment, so the <c>[]u8</c> / <c>Alignment</c> are dropped.</summary>
    private static void CHeapFree(void* ctx, Slice<byte> memory, Alignment alignment, ulong retAddr) => Libc.free(memory.Ptr);

    /// <summary>Materialize the C-heap default as a runtime <see cref="Allocator"/> value —
    /// used when the statically-known default is passed to an opaque
    /// <c>std.mem.Allocator</c> sink (a parameter / return), where it must become a real
    /// fat-pointer value. Its vtable still reaches <see cref="Libc.malloc"/>/<see cref="Libc.free"/>.</summary>
    public static Allocator CHeap()
        => new() { Ctx = null, Vtable = new AllocatorVTable { alloc = &CHeapAlloc, resize = &CHeapResize, remap = &CHeapRemap, free = &CHeapFree } };

    /// <summary>The <b>devirtualized</b> <c>page_allocator.alloc(T, n)</c> — a direct
    /// <see cref="Libc.malloc"/>, no vtable load. Emitted whenever the lowering proves the
    /// allocator is the statically-known C-heap default.</summary>
    public static ErrUnion<Slice<T>> AllocCHeap<T>(ulong n, ushort oom) where T : unmanaged
    {
        ulong bytes = n * (ulong)sizeof(T);
        byte* p = bytes > int.MaxValue ? null : (byte*)Libc.malloc((int)bytes);
        return p == null
            ? ErrUnion<Slice<T>>.Err(oom)
            : ErrUnion<Slice<T>>.Ok(new Slice<T>((T*)p, n));
    }

    /// <summary>The <b>devirtualized</b> <c>page_allocator.free(slice)</c> — a direct
    /// <see cref="Libc.free"/>.</summary>
    public static void FreeCHeap<T>(Slice<T> s) where T : unmanaged => Libc.free(s.Ptr);

    /// <summary>The <b>devirtualized</b> <c>page_allocator.create(T)</c> — a direct
    /// <see cref="Libc.malloc"/> of <c>sizeof(T)</c> bytes, no vtable. The address is carried as a
    /// <c>nuint</c> (see <see cref="Allocator.Create{T}"/> for why).</summary>
    public static ErrUnion<nuint> CreateCHeap<T>(ushort oom) where T : unmanaged
    {
        byte* p = (byte*)Libc.malloc(sizeof(T));
        return p == null ? ErrUnion<nuint>.Err(oom) : ErrUnion<nuint>.Ok((nuint)p);
    }

    /// <summary>The <b>devirtualized</b> <c>page_allocator.destroy(p)</c> — a direct
    /// <see cref="Libc.free"/>.</summary>
    public static void DestroyCHeap<T>(T* p) where T : unmanaged => Libc.free(p);

    // ---- FixedBufferAllocator (the second allocator) ---------------------

    /// <summary>Raw FBA allocation — align the next pointer up to the requested
    /// <paramref name="alignment"/> (exactly as real zig's <c>alignPointerOffset</c> does: the pad
    /// is charged to the bump cursor), then bump <see cref="FixedBufferAllocator.EndIndex"/>. Returns
    /// null when the aligned request would overflow the buffer (Zig's <c>error.OutOfMemory</c>).</summary>
    private static byte* FbaAlloc(void* ctx, ulong len, Alignment alignment, ulong retAddr)
    {
        var self = (FixedBufferAllocator*)ctx;
        ulong a = alignment.toByteUnits();
        if (a < 1) { a = 1; }
        // Offset needed to align (buffer base + cursor) up to `a`; real zig aligns the POINTER, not
        // just the index, so the buffer's own base address participates.
        nuint cur = (nuint)(self->Buffer + self->EndIndex);
        ulong pad = (ulong)((((cur + (nuint)a - 1) & ~((nuint)a - 1))) - cur);
        ulong adjusted = self->EndIndex + pad;
        ulong end = adjusted + len;
        if (end > self->Capacity) { return null; }
        byte* p = self->Buffer + adjusted;
        self->EndIndex = end;
        return p;
    }

    /// <summary>Raw FBA resize — change a block's length IN PLACE (no move), returning whether it
    /// succeeded, exactly as real zig's <c>FixedBufferAllocator.resize</c>. The LAST allocation can
    /// grow (if the buffer has room) or shrink (rewinding the cursor); any earlier block can only
    /// shrink in place (a grow would collide with the block above it) — a grow of a non-last block
    /// fails, a shrink is a no-op success. Deterministic (no page rounding), which is why resize is
    /// wired for a provable FBA but stays deferred for the C heap / opaque allocators.</summary>
    private static CBool FbaResize(void* ctx, Slice<byte> memory, Alignment alignment, ulong newLen, ulong retAddr)
    {
        var self = (FixedBufferAllocator*)ctx;
        bool isLast = (byte*)memory.Ptr + memory.Len == self->Buffer + self->EndIndex;
        if (!isLast)
        {
            return newLen <= memory.Len;   // a non-last block: shrink is a no-op success, grow fails
        }
        if (newLen <= memory.Len)
        {
            self->EndIndex -= memory.Len - newLen;   // shrink the last block: rewind the cursor
            return true;
        }
        ulong add = newLen - memory.Len;
        if (self->EndIndex + add > self->Capacity) { return false; }   // grow the last block if room
        self->EndIndex += add;
        return true;
    }

    /// <summary>Raw FBA remap — resize-possibly-moving; returns the (unchanged) pointer on success or
    /// null. An FBA never moves a block, so remap IS resize: real zig's
    /// <c>return if (resize(…)) memory.ptr else null;</c>.</summary>
    private static byte* FbaRemap(void* ctx, Slice<byte> memory, Alignment alignment, ulong newLen, ulong retAddr)
        => FbaResize(ctx, memory, alignment, newLen, retAddr) != 0 ? (byte*)memory.Ptr : null;

    /// <summary>Raw FBA free — reclaim the space iff this is the LAST allocation (real zig's
    /// <c>isLastAllocation</c> trick: the freed region ends exactly at the bump cursor, so the cursor
    /// rewinds by its length). Freeing any earlier region is a no-op — a bump allocator can only pop
    /// the top. The alignment pad ahead of the region is intentionally not reclaimed (matches zig).</summary>
    private static void FbaFree(void* ctx, Slice<byte> memory, Alignment alignment, ulong retAddr)
    {
        var self = (FixedBufferAllocator*)ctx;
        if ((byte*)memory.Ptr + memory.Len == self->Buffer + self->EndIndex)
        {
            self->EndIndex -= memory.Len;
        }
    }

    /// <summary><c>fba.allocator()</c> — produce an <see cref="Allocator"/> fat pointer whose
    /// context is the FBA and whose vtable bumps it. The caller passes <c>&amp;fba</c>, so the
    /// returned allocator is valid only while that <see cref="FixedBufferAllocator"/> local is
    /// alive (the same stack-lifetime rule as Zig).</summary>
    public static Allocator FbaAllocator(FixedBufferAllocator* self)
        => new() { Ctx = self, Vtable = new AllocatorVTable { alloc = &FbaAlloc, resize = &FbaResize, remap = &FbaRemap, free = &FbaFree } };

    /// <summary>The <b>FBA-site-devirtualized</b> <c>a.alloc(T, n)</c> (Milestone U) — a direct FBA
    /// bump with no vtable load, emitted when the lowering proves <c>a</c> is a particular
    /// <c>fba.allocator()</c> result. The <c>&amp;fba</c> context is passed explicitly.</summary>
    public static ErrUnion<Slice<T>> AllocFba<T>(FixedBufferAllocator* self, ulong n, ushort oom) where T : unmanaged
    {
        ulong bytes = n * (ulong)sizeof(T);
        byte* p = FbaAlloc(self, bytes, AlignOf<T>(), 0);
        return p == null
            ? ErrUnion<Slice<T>>.Err(oom)
            : ErrUnion<Slice<T>>.Ok(new Slice<T>((T*)p, n));
    }

    /// <summary>The FBA-site-devirtualized <c>a.free(slice)</c> — reclaims the space iff it is the
    /// last allocation, mirroring <see cref="FbaFree"/> (real zig's <c>isLastAllocation</c>).</summary>
    public static void FreeFba<T>(FixedBufferAllocator* self, Slice<T> s) where T : unmanaged
    {
        ulong bytes = s.Len * (ulong)sizeof(T);
        if ((byte*)s.Ptr + bytes == self->Buffer + self->EndIndex)
        {
            self->EndIndex -= bytes;
        }
    }

    /// <summary>The FBA-site-devirtualized <c>a.create(T)</c> (Milestone U) — a direct FBA bump of
    /// <c>sizeof(T)</c> bytes; the address rides as a <c>nuint</c> (see <see cref="Allocator.Create{T}"/>).</summary>
    public static ErrUnion<nuint> CreateFba<T>(FixedBufferAllocator* self, ushort oom) where T : unmanaged
    {
        byte* p = FbaAlloc(self, (ulong)sizeof(T), AlignOf<T>(), 0);
        return p == null ? ErrUnion<nuint>.Err(oom) : ErrUnion<nuint>.Ok((nuint)p);
    }

    /// <summary>The FBA-site-devirtualized <c>a.destroy(p)</c> — reclaims the object iff it is the
    /// last allocation, mirroring <see cref="FreeFba{T}"/>.</summary>
    public static void DestroyFba<T>(FixedBufferAllocator* self, T* p) where T : unmanaged
    {
        ulong bytes = (ulong)sizeof(T);
        if ((byte*)p + bytes == self->Buffer + self->EndIndex)
        {
            self->EndIndex -= bytes;
        }
    }

    // ---- realloc (Milestone U) -------------------------------------------

    /// <summary>The DEVIRTUALIZED C-heap <c>a.realloc(slice, n)</c> — a direct
    /// <see cref="Libc.realloc"/> (which preserves contents up to the smaller of old/new). Returns a
    /// new <see cref="Slice{T}"/> of <paramref name="n"/> elements, or <paramref name="oom"/> on
    /// failure (incl. a byte count past <see cref="int.MaxValue"/>, since <see cref="Libc.realloc"/>
    /// takes an <c>int</c>).</summary>
    public static ErrUnion<Slice<T>> ReallocCHeap<T>(Slice<T> old, ulong n, ushort oom) where T : unmanaged
    {
        nuint bytes = (nuint)n * (nuint)sizeof(T);
        if (bytes > int.MaxValue) { return ErrUnion<Slice<T>>.Err(oom); }
        void* p = Libc.realloc(old.Ptr, (int)bytes);
        return p == null
            ? ErrUnion<Slice<T>>.Err(oom)
            : ErrUnion<Slice<T>>.Ok(new Slice<T>((T*)p, n));
    }

    /// <summary>The FBA-site-devirtualized <c>a.realloc(slice, n)</c> — EMULATED (an FBA has no
    /// in-place grow): bump a fresh region and copy the preserved prefix. The old region is left in
    /// the buffer (an FBA reclaims only by reset), matching <see cref="FbaFree"/>.</summary>
    public static ErrUnion<Slice<T>> ReallocFba<T>(FixedBufferAllocator* self, Slice<T> old, ulong n, ushort oom) where T : unmanaged
    {
        ulong bytes = n * (ulong)sizeof(T);
        byte* p = FbaAlloc(self, bytes, AlignOf<T>(), 0);
        if (p == null) { return ErrUnion<Slice<T>>.Err(oom); }
        ulong keep = old.Len < n ? old.Len : n;
        System.Buffer.MemoryCopy(old.Ptr, p, (long)bytes, (long)(keep * (ulong)sizeof(T)));
        return ErrUnion<Slice<T>>.Ok(new Slice<T>((T*)p, n));
    }

    /// <summary>The FBA-site-devirtualized <c>a.resize(slice, n)</c> — a direct <see cref="FbaResize"/>
    /// over the <c>&amp;fba</c> context; returns whether the block was resized in place (Zig's
    /// <c>bool</c>). No allocation, no move: the caller keeps using <c>slice.ptr[0..n]</c> on success.</summary>
    public static CBool ResizeFba<T>(FixedBufferAllocator* self, Slice<T> s, ulong n) where T : unmanaged
        => FbaResize(self, new Slice<byte>((byte*)s.Ptr, s.Len * (ulong)sizeof(T)), AlignOf<T>(), n * (ulong)sizeof(T), 0);

    /// <summary>The FBA-site-devirtualized <c>a.remap(slice, n)</c> — a direct <see cref="FbaRemap"/>.
    /// Returns the resized slice (same pointer — an FBA never moves) on success, or <c>null</c> (Zig's
    /// <c>?[]T</c> none) when the in-place resize can't be honored.</summary>
    public static Slice<T>? RemapFba<T>(FixedBufferAllocator* self, Slice<T> s, ulong n) where T : unmanaged
    {
        byte* p = FbaRemap(self, new Slice<byte>((byte*)s.Ptr, s.Len * (ulong)sizeof(T)), AlignOf<T>(), n * (ulong)sizeof(T), 0);
        return p == null ? null : new Slice<T>((T*)p, n);
    }

    // ---- ArenaAllocator (the third allocator, Milestone U) ---------------

    /// <summary>The 16-byte-aligned distance from a chunk's start to its usable space.
    /// <see cref="ArenaChunk"/> is 24 bytes on 64-bit; rounding to 32 keeps the data start
    /// 16-aligned (malloc returns ≥16-aligned), so every handed-out pointer is 16-aligned.</summary>
    internal const int ArenaHeaderBytes = 32;

    /// <summary>The default usable capacity of a freshly-grown arena chunk (a larger request grows a
    /// chunk sized to fit it instead).</summary>
    private const nuint ArenaDefaultChunk = 4096;

    /// <summary>Raw arena allocation — bump within the current chunk, growing a new one from the
    /// backing allocator when the request doesn't fit. Requests are 16-byte-aligned so mixed-type
    /// allocations stay aligned. Returns null only when the backing allocator is exhausted.</summary>
    private static byte* ArenaAlloc(void* ctx, ulong len, Alignment alignment, ulong retAddr)
    {
        var self = (ArenaAllocator*)ctx;
        nuint need = ((nuint)len + 15) & ~(nuint)15;   // round up to a 16-byte multiple
        if (self->Current == null || self->Current->Used + need > self->Current->Cap)
        {
            nuint cap = need > ArenaDefaultChunk ? need : ArenaDefaultChunk;
            byte* raw = self->Backing.Vtable.alloc(self->Backing.Ctx, (ulong)((nuint)ArenaHeaderBytes + cap), alignment, 0);
            if (raw == null) { return null; }
            var ch = (ArenaChunk*)raw;
            ch->Prev = self->Current;
            ch->Cap = cap;
            ch->Used = 0;
            self->Current = ch;
        }
        byte* p = (byte*)self->Current + ArenaHeaderBytes + self->Current->Used;
        self->Current->Used += need;
        return p;
    }

    /// <summary>Raw arena resize/remap — never succeeds in place (false / null); part of the 4-fn
    /// vtable shape (see <see cref="AllocatorVTable.resize"/>).</summary>
    private static CBool ArenaResize(void* ctx, Slice<byte> memory, Alignment alignment, ulong newLen, ulong retAddr) => false;
    private static byte* ArenaRemap(void* ctx, Slice<byte> memory, Alignment alignment, ulong newLen, ulong retAddr) => null;

    /// <summary>Raw arena free — a no-op (an arena reclaims wholesale in <see cref="ArenaAllocator.Deinit"/>).</summary>
    private static void ArenaFree(void* ctx, Slice<byte> memory, Alignment alignment, ulong retAddr) { }

    /// <summary><c>arena.allocator()</c> — produce an <see cref="Allocator"/> fat pointer whose
    /// context is the arena and whose vtable bump-allocates it. Valid only while the
    /// <see cref="ArenaAllocator"/> local is alive (the same stack-lifetime rule as Zig).</summary>
    public static Allocator ArenaToAllocator(ArenaAllocator* self)
        => new() { Ctx = self, Vtable = new AllocatorVTable { alloc = &ArenaAlloc, resize = &ArenaResize, remap = &ArenaRemap, free = &ArenaFree } };

    /// <summary><c>arena.deinit()</c> — free the whole chunk chain. A static wrapper over
    /// <see cref="ArenaAllocator.Deinit"/> (called by-ref on the local, like <see cref="ArenaToAllocator"/>).</summary>
    public static void ArenaDeinit(ArenaAllocator* self) => self->Deinit();

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
