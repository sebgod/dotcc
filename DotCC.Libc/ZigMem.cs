#nullable enable

namespace DotCC.Libc;

/// <summary>
/// The runtime backing for the Zig front-end's curated <c>std.mem</c> helpers and the
/// <c>@memcpy</c>/<c>@memset</c> mem-builtins. Auto-spliced into every emitted program (the
/// <c>DotCC.Libc/*.cs</c> <c>&lt;EmbeddedResource&gt;</c> glob) and compiled into
/// <c>DotCC.Libc.dll</c> for the unit tests, exactly like <see cref="Slice{T}"/> /
/// <see cref="ZigAlloc"/>.
/// </summary>
/// <remarks>
/// dotcc does not model <c>std</c> in general; it lowers a curated set of the most common slice
/// utilities onto these faithful primitives. Each is generic over the (unmanaged) element type —
/// the Zig-lowering emits <c>ZigMem.{Method}&lt;T&gt;(…)</c> with the element type baked in by the
/// C# backend (see <c>DotCC.Ir.ZigMemCall</c>).
/// </remarks>
public static class ZigMem
{
    /// <summary><c>std.mem.eql(T, a, b)</c> — element-wise equality of two slices: equal length
    /// AND identical contents. Compared BYTE-wise (via
    /// <see cref="System.Runtime.InteropServices.MemoryMarshal.AsBytes{T}(System.ReadOnlySpan{T})"/>),
    /// which equals element equality for the scalar element types Zig <c>eql</c> is used with (a
    /// packed slice of a primitive has no padding). <c>AsSpan</c> has a safe (span) return type, so
    /// this method needs no <c>unsafe</c> context.</summary>
    public static bool Eql<T>(ConstSlice<T> a, ConstSlice<T> b) where T : unmanaged
    {
        if (a.Len != b.Len) { return false; }
        var ab = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan());
        var bb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan());
        return ab.SequenceEqual(bb);
    }

    /// <summary><c>std.mem.copyForwards(T, dest, source)</c> — and <c>@memcpy(dest, source)</c> —
    /// copy <c>source.len</c> elements into <c>dest</c> from front to back. The ascending order is
    /// exactly Zig's <c>copyForwards</c>, and is equally correct for the non-overlapping
    /// <c>@memcpy</c>. <c>dest.len &gt;= source.len</c> is the caller's contract, as in Zig.</summary>
    public static unsafe void CopyForwards<T>(Slice<T> dest, ConstSlice<T> source) where T : unmanaged
    {
        T* d = dest.Ptr;
        T* s = source.Ptr;
        for (ulong i = 0; i < source.Len; i++) { d[i] = s[i]; }
    }

    /// <summary><c>@memcpy(dest, source)</c> with a many-item pointer SOURCE (<c>items.ptr</c>, std.Deque, task #194):
    /// <c>dest.len</c> elements are copied, as zig takes the length from the operand that has one.</summary>
    public static unsafe void CopyForwards<T>(Slice<T> dest, T* source) where T : unmanaged
    {
        T* d = dest.Ptr;
        for (ulong i = 0; i < dest.Len; i++) { d[i] = source[i]; }
    }

    /// <summary><c>@memcpy(dest, source)</c> with a many-item pointer DEST: <c>source.len</c> elements are copied.</summary>
    public static unsafe void CopyForwards<T>(T* dest, ConstSlice<T> source) where T : unmanaged
    {
        T* s = source.Ptr;
        for (ulong i = 0; i < source.Len; i++) { dest[i] = s[i]; }
    }

    /// <summary><see cref="CopyForwards{T}(Slice{T}, ConstSlice{T})"/> over a slice of POINTERS (a <c>[N]?*T</c> array
    /// copy, std.Treap's <c>new.children = old.children</c>): C# takes no pointer type argument, so the type argument is
    /// the pointee.</summary>
    public static unsafe void CopyForwards<T>(PtrSlice<T> dest, ConstPtrSlice<T> source) where T : unmanaged
    {
        T** d = dest.Ptr;
        T** s = source.Ptr;
        for (ulong i = 0; i < source.Len; i++) { d[i] = s[i]; }
    }

    /// <summary>zig <c>@constCast</c> of a <c>[]const T</c> slice: the <c>[]T</c> over the same pointer and length.</summary>
    public static unsafe Slice<T> ConstCast<T>(ConstSlice<T> slice) where T : unmanaged => new(slice.Ptr, slice.Len);

    /// <summary><c>@memmove(dest, source)</c> — copy <c>source.len</c> elements into <c>dest</c> where the two
    /// may OVERLAP (array_list's in-place shifts): the direction is chosen so every element is read before it
    /// is overwritten, as C's <c>memmove</c>.</summary>
    public static unsafe void Move<T>(Slice<T> dest, ConstSlice<T> source) where T : unmanaged
    {
        T* d = dest.Ptr;
        T* s = source.Ptr;
        if (d <= s)
        {
            for (ulong i = 0; i < source.Len; i++) { d[i] = s[i]; }
        }
        else
        {
            for (ulong i = source.Len; i > 0; i--) { d[i - 1] = s[i - 1]; }
        }
    }

    /// <summary><c>@memmove(dest, source)</c> with a many-item pointer SOURCE: <c>dest.len</c> elements, overlap-safe.</summary>
    public static unsafe void Move<T>(Slice<T> dest, T* source) where T : unmanaged
        => Move(dest, new ConstSlice<T>(source, dest.Len));

    /// <summary><c>@memmove(dest, source)</c> with a many-item pointer DEST: <c>source.len</c> elements, overlap-safe.</summary>
    public static unsafe void Move<T>(T* dest, ConstSlice<T> source) where T : unmanaged
        => Move(new Slice<T>(dest, source.Len), source);

    /// <summary><see cref="Move{T}(Slice{T}, ConstSlice{T})"/> over a slice of POINTERS; the type argument is the
    /// pointee.</summary>
    public static unsafe void Move<T>(PtrSlice<T> dest, ConstPtrSlice<T> source) where T : unmanaged
    {
        T** d = dest.Ptr;
        T** s = source.Ptr;
        if (d <= s)
        {
            for (ulong i = 0; i < source.Len; i++) { d[i] = s[i]; }
        }
        else
        {
            for (ulong i = source.Len; i > 0; i--) { d[i - 1] = s[i - 1]; }
        }
    }

    /// <summary><c>@memset(dest, value)</c> — set every element of <c>dest</c> to
    /// <paramref name="value"/>.</summary>
    public static unsafe void Set<T>(Slice<T> dest, T value) where T : unmanaged
    {
        T* d = dest.Ptr;
        for (ulong i = 0; i < dest.Len; i++) { d[i] = value; }
    }

    /// <summary><see cref="Set{T}(Slice{T}, T)"/> over a slice of POINTERS (<c>@memset(&amp;ptrs, null)</c>); the type
    /// argument is the pointee.</summary>
    public static unsafe void Set<T>(PtrSlice<T> dest, T* value) where T : unmanaged
    {
        T** d = dest.Ptr;
        for (ulong i = 0; i < dest.Len; i++) { d[i] = value; }
    }

    /// <summary><c>std.mem.span(ptr)</c> for a NUL-sentinel pointer (<c>[*:0]T</c>, dotcc's V1
    /// sentinel = 0) — scan to the first all-zero element and return the <c>[]const T</c> slice
    /// BEFORE it (the length excludes the sentinel, exactly as Zig). The scan is byte-wise so it
    /// covers any unmanaged element type with a zero sentinel; the common case is the <c>char*</c>
    /// C-string <c>[*:0]const u8</c>. Returns a const slice (dotcc erases the sentinel, so a mutable
    /// <c>[:0]T</c> result is a documented cut).</summary>
    public static unsafe ConstSlice<T> SpanZ<T>(T* p) where T : unmanaged
    {
        ulong n = 0;
        while (!IsZeroElem(p + n)) { n++; }
        return new ConstSlice<T>(p, n);
    }

    /// <summary><c>std.mem.sliceTo(s, end)</c> over a const slice (or an array / string literal viewed as one): the
    /// elements before the first one equal to <paramref name="end"/>, or all of them when none is (task #127).</summary>
    public static unsafe ConstSlice<T> SliceTo<T>(ConstSlice<T> s, T end) where T : unmanaged
    {
        ulong n = 0;
        while (n < s.Len && !SameElem(s.Ptr + n, &end)) { n++; }
        return new ConstSlice<T>(s.Ptr, n);
    }

    /// <summary><c>std.mem.sliceTo(s, end)</c> over a mutable slice: the same prefix, still mutable, as zig preserves
    /// the pointer's constness.</summary>
    public static unsafe Slice<T> SliceTo<T>(Slice<T> s, T end) where T : unmanaged
    {
        ulong n = 0;
        while (n < s.Len && !SameElem(s.Ptr + n, &end)) { n++; }
        return new Slice<T>(s.Ptr, n);
    }

    /// <summary><c>std.mem.sliceTo(p, end)</c> over a many-item pointer (<c>[*:end]T</c> / <c>[*c]T</c>): unbounded, the
    /// elements before the first one equal to <paramref name="end"/>, which zig takes as the pointer's sentinel.</summary>
    public static unsafe ConstSlice<T> SliceToSentinel<T>(T* p, T end) where T : unmanaged
    {
        ulong n = 0;
        while (!SameElem(p + n, &end)) { n++; }
        return new ConstSlice<T>(p, n);
    }

    /// <summary>Byte-wise equality of two elements, AOT-clean for any unmanaged type (no equality constraint), as
    /// <see cref="IsZeroElem{T}"/> is for the zero sentinel.</summary>
    private static unsafe bool SameElem<T>(T* a, T* b) where T : unmanaged
    {
        byte* x = (byte*)a;
        byte* y = (byte*)b;
        for (int k = 0; k < sizeof(T); k++) { if (x[k] != y[k]) { return false; } }
        return true;
    }

    /// <summary>True when the element at <paramref name="p"/> is all-zero bytes — the sentinel test
    /// for <see cref="SpanZ{T}"/>. Byte-wise so it is AOT-clean and works for any unmanaged
    /// element type without an equality constraint.</summary>
    private static unsafe bool IsZeroElem<T>(T* p) where T : unmanaged
    {
        byte* b = (byte*)p;
        for (int k = 0; k < sizeof(T); k++) { if (b[k] != 0) { return false; } }
        return true;
    }

    /// <summary>The payload of a value optional, IN PLACE: zig's by-ref capture <c>if (opt) |*v|</c> (std.enums.EnumMap)
    /// points <c>v</c> at the optional's own payload, so a write through <c>v</c> changes the optional and a later write to
    /// the optional is seen through <c>v</c>. <see cref="System.Nullable.GetValueRefOrDefaultRef{T}"/> is the BCL's ref to
    /// that field (no layout assumption); the caller has already tested <c>HasValue</c>.</summary>
    public static unsafe T* OptionalPayload<T>(T?* optional) where T : unmanaged
        => (T*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref System.Runtime.CompilerServices.Unsafe.AsRef(in System.Nullable.GetValueRefOrDefaultRef(in *optional)));
}
