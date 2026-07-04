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

    /// <summary><c>@memset(dest, value)</c> — set every element of <c>dest</c> to
    /// <paramref name="value"/>.</summary>
    public static unsafe void Set<T>(Slice<T> dest, T value) where T : unmanaged
    {
        T* d = dest.Ptr;
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

    /// <summary>True when the element at <paramref name="p"/> is all-zero bytes — the sentinel test
    /// for <see cref="SpanZ{T}"/>. Byte-wise so it is AOT-clean and works for any unmanaged
    /// element type without an equality constraint.</summary>
    private static unsafe bool IsZeroElem<T>(T* p) where T : unmanaged
    {
        byte* b = (byte*)p;
        for (int k = 0; k < sizeof(T); k++) { if (b[k] != 0) { return false; } }
        return true;
    }
}
