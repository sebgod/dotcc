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
