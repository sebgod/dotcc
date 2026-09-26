#nullable enable

namespace DotCC.Libc;

/// <summary>
/// The runtime backing for the Zig front-end's curated <c>std.ArrayList(T)</c> (wall-plan W0) —
/// the modern UNMANAGED array list (zig 0.15+ re-pointed <c>std.ArrayList</c> at the unmanaged
/// variant: no stored allocator, every growing call takes one explicitly). Auto-spliced into
/// every emitted program (the <c>DotCC.Libc/*.cs</c> <c>&lt;EmbeddedResource&gt;</c> glob) and
/// compiled into <c>DotCC.Libc.dll</c> for the unit tests, exactly like <see cref="Slice{T}"/> /
/// <see cref="ZigAlloc"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a CURATED runtime type, not a transpilation of zig's <c>array_list.zig</c> (which is
/// comptime-soaked): dotcc authors the body, so an open C# generic works — the same reasoning as
/// <see cref="Slice{T}"/>. The lowering resolves the type <c>std.ArrayList(i32)</c> to
/// <c>ZigList&lt;int&gt;</c> and routes the curated member set here; an unmodeled member is a
/// clear error at the use site.
/// </para>
/// <para>
/// Zig's <c>.empty</c> decl literal is exactly <c>default</c>: a null pointer with zero
/// length/capacity (the first growing call allocates). Methods that grow return <c>!void</c>
/// (<c>ErrUnion&lt;Unit&gt;</c>, <c>error.OutOfMemory</c> on an exhausted allocator) so
/// <c>try list.append(alloc, v)</c> composes with the existing error-union machinery. Mutating
/// methods are INSTANCE methods called on an lvalue receiver — a C# struct method mutates the
/// receiver variable in place, matching zig's <c>*Self</c> methods on an addressable list.
/// </para>
/// <para>
/// Mirrors <see cref="Slice{T}"/>'s representation choices: the data pointer is stored as
/// <see cref="nint"/> so the type needs no type-level <c>unsafe</c> (only the members that
/// touch elements are <c>unsafe</c>), and the length/capacity are <c>ulong</c> (Zig's
/// <c>usize</c> on dotcc's LP64 target).
/// </para>
/// </remarks>
public struct ZigList<T> where T : unmanaged
{
    private nint _ptr;

    /// <summary>The element count (zig <c>list.items.len</c>).</summary>
    public ulong Len;

    /// <summary>The allocated capacity in elements (zig <c>list.capacity</c>).</summary>
    public ulong Cap;

    /// <summary>The data pointer (null until the first growing call).</summary>
    public unsafe T* Ptr => (T*)_ptr;

    /// <summary>Zig's <c>list.items</c> — the occupied prefix as a mutable slice, so
    /// <c>list.items[i]</c> / <c>list.items.len</c> ride the existing slice lowering.</summary>
    public unsafe Slice<T> Items => new((T*)_ptr, Len);

    /// <summary>zig's <c>std.atomic.cache_line</c> for the host: 128 bytes on x86_64 and aarch64 (a pair of prefetched
    /// 64-byte lines), 64 elsewhere. It sets the first allocation's size.</summary>
    private static ulong CacheLine =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64
            ? 128UL : 64UL;

    /// <summary>zig's <c>growCapacity(minimum)</c>: <c>minimum +| (minimum / 2 + init_capacity)</c>, with
    /// <c>init_capacity = @max(1, cache_line / @sizeOf(T))</c>. The capacity a list grows to is observable, through the
    /// point where a bounded allocator (a FixedBufferAllocator) runs out (task #145), so it follows zig exactly.</summary>
    private static unsafe ulong GrowCapacity(ulong minimum)
    {
        ulong init = System.Math.Max(1UL, CacheLine / (ulong)sizeof(T));
        ulong add = minimum / 2 + init;
        return ulong.MaxValue - minimum < add ? ulong.MaxValue : minimum + add;
    }

    /// <summary>zig's <c>ensureTotalCapacityPrecise</c>: exactly <paramref name="n"/> elements. It first tries to
    /// <c>remap</c> the allocation in place (a non-empty one; zig's remap of an empty slice is null), else allocates
    /// afresh, copies the occupied prefix and frees the old block.</summary>
    private unsafe ErrUnion<Unit> EnsureTotalCapacityPrecise(Allocator a, ulong n, ushort oom)
    {
        if (Cap >= n) { return ErrUnion<Unit>.Ok(default); }
        if (Cap != 0 && a.Remap(new Slice<T>((T*)_ptr, Cap), n) is { } remapped)
        {
            _ptr = (nint)remapped.Ptr;
            Cap = remapped.Len;
            return ErrUnion<Unit>.Ok(default);
        }
        var fresh = a.Alloc<T>(n, oom);
        if (fresh.IsErr) { return ErrUnion<Unit>.Err(fresh.Code); }
        long bytes = (long)(Len * (ulong)sizeof(T));
        if (Len != 0) { System.Buffer.MemoryCopy((T*)_ptr, fresh.Value.Ptr, bytes, bytes); }
        if (Cap != 0) { a.Free(new Slice<T>((T*)_ptr, Cap)); }
        _ptr = (nint)fresh.Value.Ptr;
        Cap = n;
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig's <c>ensureTotalCapacity</c>: nothing when <paramref name="need"/> already fits, else the precise
    /// growth to <see cref="GrowCapacity"/>(<paramref name="need"/>).</summary>
    private ErrUnion<Unit> EnsureCap(Allocator a, ulong need, ushort oom)
        => Cap >= need ? ErrUnion<Unit>.Ok(default) : EnsureTotalCapacityPrecise(a, GrowCapacity(need), oom);

    /// <summary>zig's <c>addManyAt(index, count)</c>, behind <c>insert</c> / <c>insertSlice</c>: room for
    /// <paramref name="count"/> elements at <paramref name="index"/>, the tail moved up. Past capacity it grows to
    /// <see cref="GrowCapacity"/>(new length), by an in-place remap or else a fresh block the head and tail are copied
    /// around (no copy of the old capacity), the old block then freed.</summary>
    private unsafe ErrUnion<Unit> AddManyAt(Allocator a, ulong index, ulong count, ushort oom)
    {
        if (ulong.MaxValue - Len < count) { return ErrUnion<Unit>.Err(oom); }
        ulong newLen = Len + count;
        long tailBytes = (long)((Len - index) * (ulong)sizeof(T));
        if (Cap < newLen)
        {
            ulong newCap = GrowCapacity(newLen);
            if (Cap != 0 && a.Remap(new Slice<T>((T*)_ptr, Cap), newCap) is { } remapped)
            {
                _ptr = (nint)remapped.Ptr;
                Cap = remapped.Len;
            }
            else
            {
                var fresh = a.Alloc<T>(newCap, oom);
                if (fresh.IsErr) { return ErrUnion<Unit>.Err(fresh.Code); }
                var q = fresh.Value.Ptr;
                long headBytes = (long)(index * (ulong)sizeof(T));
                if (index != 0) { System.Buffer.MemoryCopy((T*)_ptr, q, headBytes, headBytes); }
                if (tailBytes != 0) { System.Buffer.MemoryCopy((T*)_ptr + index, q + index + count, tailBytes, tailBytes); }
                if (Cap != 0) { a.Free(new Slice<T>((T*)_ptr, Cap)); }
                _ptr = (nint)q;
                Cap = newCap;
                Len = newLen;
                return ErrUnion<Unit>.Ok(default);
            }
        }
        var p = (T*)_ptr;
        if (tailBytes != 0) { System.Buffer.MemoryCopy(p + index, p + index + count, tailBytes, tailBytes); }
        Len = newLen;
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig <c>list.append(alloc, item)</c> — append one element, growing as needed.
    /// Returns <c>!void</c>: <c>error.OutOfMemory</c> (<paramref name="oom"/>) when the
    /// allocator is exhausted.</summary>
    public unsafe ErrUnion<Unit> Append(Allocator a, T item, ushort oom)
    {
        var ok = EnsureCap(a, Len + 1, oom);
        if (ok.IsErr) { return ok; }
        ((T*)_ptr)[Len] = item;
        Len += 1;
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig <c>list.appendSlice(alloc, s)</c> — append every element of a slice.
    /// Takes <see cref="ConstSlice{T}"/> (a mutable <see cref="Slice{T}"/> coerces implicitly,
    /// the same <c>[]T</c> → <c>[]const T</c> rule as zig).</summary>
    public unsafe ErrUnion<Unit> AppendSlice(Allocator a, ConstSlice<T> s, ushort oom)
    {
        var ok = EnsureUnusedCapacity(a, s.Len, oom);
        if (ok.IsErr) { return ok; }
        long bytes = (long)(s.Len * (ulong)sizeof(T));
        System.Buffer.MemoryCopy(s.Ptr, (T*)_ptr + Len, bytes, bytes);
        Len += s.Len;
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig <c>list.pop()</c> — remove and return the last element, or <c>null</c> when
    /// the list is empty (zig 0.15+ changed pop's return to <c>?T</c>).</summary>
    public unsafe T? Pop()
    {
        if (Len == 0) { return null; }
        Len -= 1;
        return ((T*)_ptr)[Len];
    }

    /// <summary>zig <c>list.clearRetainingCapacity()</c> — drop the elements, keep the store.</summary>
    public void ClearRetainingCapacity() => Len = 0;

    /// <summary>zig <c>list.insert(alloc, i, item)</c> — insert at <paramref name="i"/>, shifting the tail up by one.</summary>
    public unsafe ErrUnion<Unit> Insert(Allocator a, ulong i, T item, ushort oom)
    {
        if (i > Len) { throw new System.IndexOutOfRangeException("zig ArrayList.insert: index out of bounds"); }
        var ok = AddManyAt(a, i, 1, oom);
        if (ok.IsErr) { return ok; }
        ((T*)_ptr)[i] = item;
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig <c>list.insertSlice(alloc, i, s)</c> — insert every element of <paramref name="s"/> at
    /// <paramref name="i"/>, shifting the tail up by <c>s.len</c>. Like zig, the slice must not alias the list.</summary>
    public unsafe ErrUnion<Unit> InsertSlice(Allocator a, ulong i, ConstSlice<T> s, ushort oom)
    {
        if (i > Len) { throw new System.IndexOutOfRangeException("zig ArrayList.insertSlice: index out of bounds"); }
        var ok = AddManyAt(a, i, s.Len, oom);
        if (ok.IsErr) { return ok; }
        long bytes = (long)(s.Len * (ulong)sizeof(T));
        System.Buffer.MemoryCopy(s.Ptr, (T*)_ptr + i, bytes, bytes);
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig <c>list.orderedRemove(i)</c> — remove and return element <paramref name="i"/>, shifting the tail down.</summary>
    public unsafe T OrderedRemove(ulong i)
    {
        if (i >= Len) { throw new System.IndexOutOfRangeException("zig ArrayList.orderedRemove: index out of bounds"); }
        var p = (T*)_ptr;
        var removed = p[i];
        long bytes = (long)((Len - i - 1) * (ulong)sizeof(T));
        System.Buffer.MemoryCopy(p + i + 1, p + i, bytes, bytes);
        Len -= 1;
        return removed;
    }

    /// <summary>zig <c>list.appendNTimes(alloc, value, n)</c> — append <paramref name="n"/> copies of
    /// <paramref name="value"/>. Grows as zig's <c>resize</c> does (<c>ensureTotalCapacity</c> of the new length).</summary>
    public unsafe ErrUnion<Unit> AppendNTimes(Allocator a, T value, ulong n, ushort oom)
    {
        if (ulong.MaxValue - Len < n) { return ErrUnion<Unit>.Err(oom); }
        var ok = EnsureCap(a, Len + n, oom);
        if (ok.IsErr) { return ok; }
        AppendNTimesAssumeCapacity(value, n);
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig <c>list.appendNTimesAssumeCapacity(value, n)</c> — the same without growing; the capacity must hold them.</summary>
    public unsafe void AppendNTimesAssumeCapacity(T value, ulong n)
    {
        if (Cap - Len < n) { throw new System.InvalidOperationException("zig ArrayList.appendNTimesAssumeCapacity: capacity exceeded"); }
        var p = (T*)_ptr + Len;
        for (ulong k = 0; k < n; k++) { p[k] = value; }
        Len += n;
    }

    /// <summary>zig <c>list.replaceRange(alloc, start, len, new_items)</c> — replace the <paramref name="len"/> elements
    /// at <paramref name="start"/> by <paramref name="newItems"/>, growing to the new length first when it is longer.</summary>
    public unsafe ErrUnion<Unit> ReplaceRange(Allocator a, ulong start, ulong len, ConstSlice<T> newItems, ushort oom)
    {
        var ok = EnsureCap(a, Len - len + newItems.Len, oom);
        if (ok.IsErr) { return ok; }
        ReplaceRangeAssumeCapacity(start, len, newItems);
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig <c>list.replaceRangeAssumeCapacity(start, len, new_items)</c> — the tail after the replaced range moves
    /// to follow <paramref name="newItems"/>, and the length changes by their difference; the capacity must hold it.</summary>
    public unsafe void ReplaceRangeAssumeCapacity(ulong start, ulong len, ConstSlice<T> newItems)
    {
        if (start + len > Len) { throw new System.IndexOutOfRangeException("zig ArrayList.replaceRange: range out of bounds"); }
        ulong newLen = Len - len + newItems.Len;
        if (newLen > Cap) { throw new System.InvalidOperationException("zig ArrayList.replaceRangeAssumeCapacity: capacity exceeded"); }
        var p = (T*)_ptr;
        long tailBytes = (long)((Len - start - len) * (ulong)sizeof(T));
        if (tailBytes != 0) { System.Buffer.MemoryCopy(p + start + len, p + start + newItems.Len, tailBytes, tailBytes); }
        long newBytes = (long)(newItems.Len * (ulong)sizeof(T));
        if (newBytes != 0) { System.Buffer.MemoryCopy(newItems.Ptr, p + start, newBytes, newBytes); }
        Len = newLen;
    }

    /// <summary>zig <c>list.swapRemove(i)</c> — remove and return element <paramref name="i"/>, moving the last one into its place.</summary>
    public unsafe T SwapRemove(ulong i)
    {
        if (i >= Len) { throw new System.IndexOutOfRangeException("zig ArrayList.swapRemove: index out of bounds"); }
        var p = (T*)_ptr;
        var removed = p[i];
        p[i] = p[Len - 1];
        Len -= 1;
        return removed;
    }

    /// <summary>zig <c>list.getLast()</c> — the last element, or <c>null</c> when the list is empty (zig 0.17-dev returns
    /// <c>?T</c>).</summary>
    public unsafe T? GetLast() => Len == 0 ? null : ((T*)_ptr)[Len - 1];

    /// <summary>zig <c>list.ensureTotalCapacity(alloc, n)</c> — capacity for at least <paramref name="n"/> elements.</summary>
    public ErrUnion<Unit> EnsureTotalCapacity(Allocator a, ulong n, ushort oom) => EnsureCap(a, n, oom);

    /// <summary>zig <c>list.ensureUnusedCapacity(alloc, n)</c> — room for <paramref name="n"/> more elements.</summary>
    public ErrUnion<Unit> EnsureUnusedCapacity(Allocator a, ulong n, ushort oom)
        => ulong.MaxValue - Len < n ? ErrUnion<Unit>.Err(oom) : EnsureCap(a, Len + n, oom);

    /// <summary>zig <c>list.appendAssumeCapacity(item)</c> — append into capacity reserved earlier.</summary>
    public unsafe void AppendAssumeCapacity(T item)
    {
        if (Len >= Cap) { throw new System.InvalidOperationException("zig ArrayList.appendAssumeCapacity: no capacity left"); }
        ((T*)_ptr)[Len] = item;
        Len += 1;
    }

    /// <summary>zig <c>list.shrinkRetainingCapacity(n)</c> — keep the first <paramref name="n"/> elements.</summary>
    public void ShrinkRetainingCapacity(ulong n)
    {
        if (n > Len) { throw new System.IndexOutOfRangeException("zig ArrayList.shrinkRetainingCapacity: new length exceeds the old"); }
        Len = n;
    }

    /// <summary>zig <c>list.deinit(alloc)</c> — return the backing store to the allocator and
    /// reset to <c>.empty</c>. Idempotent (a second call sees a null pointer), mirroring
    /// <see cref="ArenaAllocator.Deinit"/>.</summary>
    public unsafe void Deinit(Allocator a)
    {
        if (_ptr != 0) { a.Free(new Slice<T>((T*)_ptr, Cap)); }
        _ptr = 0;
        Len = 0;
        Cap = 0;
    }
}
