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

    /// <summary>Grow the backing store to hold at least <paramref name="need"/> elements
    /// (doubling from 8), copying the occupied prefix through the allocator's realloc.
    /// No-op when capacity already suffices.</summary>
    private unsafe ErrUnion<Unit> EnsureCap(Allocator a, ulong need, ushort oom)
    {
        if (need <= Cap) { return ErrUnion<Unit>.Ok(default); }
        ulong newCap = Cap == 0 ? 8 : Cap * 2;
        while (newCap < need) { newCap *= 2; }
        // A fresh list allocates; a grown one reallocs (alloc+copy+free through the vtable,
        // so FBA/arena-backed lists grow correctly too).
        var grown = _ptr == 0
            ? a.Alloc<T>(newCap, oom)
            : a.Realloc(new Slice<T>((T*)_ptr, Cap), newCap, oom);
        if (grown.IsErr) { return ErrUnion<Unit>.Err(grown.Code); }
        _ptr = (nint)grown.Value.Ptr;
        Cap = newCap;
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
        var ok = EnsureCap(a, Len + s.Len, oom);
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
        var ok = EnsureCap(a, Len + 1, oom);
        if (ok.IsErr) { return ok; }
        var p = (T*)_ptr;
        long bytes = (long)((Len - i) * (ulong)sizeof(T));
        System.Buffer.MemoryCopy(p + i, p + i + 1, bytes, bytes);
        p[i] = item;
        Len += 1;
        return ErrUnion<Unit>.Ok(default);
    }

    /// <summary>zig <c>list.insertSlice(alloc, i, s)</c> — insert every element of <paramref name="s"/> at
    /// <paramref name="i"/>, shifting the tail up by <c>s.len</c>. Like zig, the slice must not alias the list.</summary>
    public unsafe ErrUnion<Unit> InsertSlice(Allocator a, ulong i, ConstSlice<T> s, ushort oom)
    {
        if (i > Len) { throw new System.IndexOutOfRangeException("zig ArrayList.insertSlice: index out of bounds"); }
        var ok = EnsureCap(a, Len + s.Len, oom);
        if (ok.IsErr) { return ok; }
        var p = (T*)_ptr;
        long tail = (long)((Len - i) * (ulong)sizeof(T));
        System.Buffer.MemoryCopy(p + i, p + i + s.Len, tail, tail);
        long bytes = (long)(s.Len * (ulong)sizeof(T));
        System.Buffer.MemoryCopy(s.Ptr, p + i, bytes, bytes);
        Len += s.Len;
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
    public ErrUnion<Unit> EnsureUnusedCapacity(Allocator a, ulong n, ushort oom) => EnsureCap(a, Len + n, oom);

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
