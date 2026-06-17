#nullable enable

namespace DotCC.Libc;

/// <summary>
/// A Zig slice <c>[]T</c> — a fat pointer <c>{ ptr, len }</c> over a contiguous run of
/// <typeparamref name="T"/>. This is the mutable form; <see cref="ConstSlice{T}"/> is
/// <c>[]const T</c>.
/// </summary>
/// <remarks>
/// <para>The representation is the C++ <c>std::span</c> shape — a plain blittable value
/// holding a data pointer and a length — deliberately <b>not</b> C#'s
/// <see cref="System.Span{T}"/>. A <see cref="System.Span{T}"/> is a <c>ref struct</c>, so
/// it cannot be a field of a normal struct (Zig slices live inside structs all the time)
/// nor cross the C/Zig ABI. A plain <c>{ ptr, len }</c> struct can do both, and its layout
/// matches Zig's own slice ABI. <see cref="System.Span{T}"/> is still available as an
/// internal bridge via <see cref="AsSpan"/> for reaching span-based BCL APIs.</para>
/// <para>The data pointer is stored as <see cref="nint"/> (identical width/representation to
/// <c>T*</c>) so the type needs no type-level <c>unsafe</c>; only the members that surface a
/// <c>T*</c> are <c>unsafe</c>. The length is a <see cref="ulong"/> — dotcc lowers Zig's
/// <c>usize</c> to <c>ulong</c> on its LP64 target, so <c>slice.len</c> reads as a
/// <c>ulong</c> with no conversion friction.</para>
/// </remarks>
public readonly struct Slice<T> where T : unmanaged
{
    private readonly nint _ptr;

    /// <summary>The element count (Zig <c>slice.len</c>).</summary>
    public readonly ulong Len;

    /// <summary>Construct a slice over <paramref name="len"/> elements at
    /// <paramref name="ptr"/>.</summary>
    public unsafe Slice(T* ptr, ulong len)
    {
        _ptr = (nint)ptr;
        Len = len;
    }

    /// <summary>The data pointer (Zig <c>slice.ptr</c>).</summary>
    public unsafe T* Ptr => (T*)_ptr;

    /// <summary>Element access (Zig <c>slice[i]</c>); returns an lvalue so
    /// <c>slice[i] = …</c> works. Not bounds-checked in this build.</summary>
    public unsafe ref T this[ulong i] => ref ((T*)_ptr)[i];

    /// <summary>The sub-slice <c>slice[lo..hi]</c>.</summary>
    public unsafe Slice<T> Sub(ulong lo, ulong hi) => new((T*)_ptr + lo, hi - lo);

    /// <summary>A <see cref="System.Span{T}"/> view, for span-based BCL APIs. The length is
    /// narrowed to <see cref="int"/> (a <c>Span</c> can only address <c>int.MaxValue</c>
    /// elements), checked.</summary>
    public unsafe System.Span<T> AsSpan() => new((void*)_ptr, checked((int)Len));

    /// <summary>A mutable slice is usable wherever a <c>[]const T</c> is expected (Zig's
    /// <c>[]T</c> → <c>[]const T</c> coercion). The reverse is not allowed.</summary>
    public static implicit operator ConstSlice<T>(Slice<T> s) => new(s._ptr, s.Len);
}

/// <summary>
/// A Zig const slice <c>[]const T</c> — the read-only counterpart of <see cref="Slice{T}"/>.
/// Same <c>{ ptr, len }</c> representation; the const-ness lives in the type, mirroring Zig
/// (and reusing dotcc's const-discard reasoning).
/// </summary>
public readonly struct ConstSlice<T> where T : unmanaged
{
    private readonly nint _ptr;

    /// <summary>The element count (Zig <c>slice.len</c>).</summary>
    public readonly ulong Len;

    /// <summary>Construct a const slice over <paramref name="len"/> elements at
    /// <paramref name="ptr"/>.</summary>
    public unsafe ConstSlice(T* ptr, ulong len)
    {
        _ptr = (nint)ptr;
        Len = len;
    }

    /// <summary>Construct from a raw stored pointer value (used by the
    /// <see cref="Slice{T}"/> → <see cref="ConstSlice{T}"/> conversion).</summary>
    internal ConstSlice(nint ptr, ulong len)
    {
        _ptr = ptr;
        Len = len;
    }

    /// <summary>The data pointer (Zig <c>slice.ptr</c>), a pointer to const.</summary>
    public unsafe T* Ptr => (T*)_ptr;

    /// <summary>Element access (Zig <c>slice[i]</c>); read-only. Not bounds-checked in this
    /// build.</summary>
    public unsafe T this[ulong i] => ((T*)_ptr)[i];

    /// <summary>The sub-slice <c>slice[lo..hi]</c>.</summary>
    public unsafe ConstSlice<T> Sub(ulong lo, ulong hi) => new((T*)_ptr + lo, hi - lo);

    /// <summary>A <see cref="System.ReadOnlySpan{T}"/> view, for span-based BCL APIs.</summary>
    public unsafe System.ReadOnlySpan<T> AsSpan() => new((void*)_ptr, checked((int)Len));
}
