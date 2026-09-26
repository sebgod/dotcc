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
/// <para>Both fields are WRITABLE, as a zig slice's are: std.ArrayList grows its <c>items</c> in place with
/// <c>self.items.len += 1</c> and re-points it with <c>self.items.ptr = new_memory.ptr</c>.</para>
/// <para>The data pointer is stored as <see cref="nint"/> (identical width/representation to
/// <c>T*</c>) so the type needs no type-level <c>unsafe</c>; only the members that surface a
/// <c>T*</c> are <c>unsafe</c>. The length is a <see cref="ulong"/> — dotcc lowers Zig's
/// <c>usize</c> to <c>ulong</c> on its LP64 target, so <c>slice.len</c> reads as a
/// <c>ulong</c> with no conversion friction.</para>
/// </remarks>
public struct Slice<T> where T : unmanaged
{
    private nint _ptr;

    /// <summary>The element count (Zig <c>slice.len</c>), writable.</summary>
    public ulong Len;

    /// <summary>Construct a slice over <paramref name="len"/> elements at
    /// <paramref name="ptr"/>.</summary>
    public unsafe Slice(T* ptr, ulong len)
    {
        _ptr = (nint)ptr;
        Len = len;
    }

    /// <summary>The data pointer (Zig <c>slice.ptr</c>), writable.</summary>
    public unsafe T* Ptr
    {
        readonly get => (T*)_ptr;
        set => _ptr = (nint)value;
    }

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
public struct ConstSlice<T> where T : unmanaged
{
    private nint _ptr;

    /// <summary>The element count (Zig <c>slice.len</c>), writable (a <c>var s: []const u8</c> can be
    /// re-pointed; its elements cannot be written).</summary>
    public ulong Len;

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

    /// <summary>The data pointer (Zig <c>slice.ptr</c>), a pointer to const; writable.</summary>
    public unsafe T* Ptr
    {
        readonly get => (T*)_ptr;
        set => _ptr = (nint)value;
    }

    /// <summary>Element access (Zig <c>slice[i]</c>); read-only. Not bounds-checked in this
    /// build.</summary>
    public unsafe T this[ulong i] => ((T*)_ptr)[i];

    /// <summary>The sub-slice <c>slice[lo..hi]</c>.</summary>
    public unsafe ConstSlice<T> Sub(ulong lo, ulong hi) => new((T*)_ptr + lo, hi - lo);

    /// <summary>A <see cref="System.ReadOnlySpan{T}"/> view, for span-based BCL APIs.</summary>
    public unsafe System.ReadOnlySpan<T> AsSpan() => new((void*)_ptr, checked((int)Len));
}

/// <summary>
/// A Zig slice of POINTERS <c>[]*T</c> / <c>[][*]T</c> (std.crypto.blake3's <c>inputs: [][*]const u8</c>): C# forbids
/// a pointer type argument, so <c>Slice&lt;T*&gt;</c> cannot exist; this is the same <c>{ ptr, len }</c> fat pointer
/// over pointees <typeparamref name="T"/>, its data pointer a <c>T**</c> and each element a <c>T*</c>.
/// </summary>
public struct PtrSlice<T> where T : unmanaged
{
    private nint _ptr;

    /// <summary>The element count (Zig <c>slice.len</c>), writable.</summary>
    public ulong Len;

    /// <summary>Construct a slice over <paramref name="len"/> pointers at <paramref name="ptr"/>.</summary>
    public unsafe PtrSlice(T** ptr, ulong len)
    {
        _ptr = (nint)ptr;
        Len = len;
    }

    /// <summary>The data pointer (Zig <c>slice.ptr</c>), writable.</summary>
    public unsafe T** Ptr
    {
        readonly get => (T**)_ptr;
        set => _ptr = (nint)value;
    }

    /// <summary>Element access (Zig <c>slice[i]</c>), an lvalue. Not bounds-checked in this build.</summary>
    public unsafe ref T* this[ulong i] => ref ((T**)_ptr)[i];

    /// <summary>The sub-slice <c>slice[lo..hi]</c>.</summary>
    public unsafe PtrSlice<T> Sub(ulong lo, ulong hi) => new((T**)_ptr + lo, hi - lo);

    /// <summary>Zig's <c>[]*T</c> → <c>[]const *T</c> coercion.</summary>
    public static implicit operator ConstPtrSlice<T>(PtrSlice<T> s) => new(s._ptr, s.Len);
}

/// <summary>The read-only counterpart of <see cref="PtrSlice{T}"/>: a Zig <c>[]const *T</c> / <c>[]const [*]T</c>.</summary>
public struct ConstPtrSlice<T> where T : unmanaged
{
    private nint _ptr;

    /// <summary>The element count (Zig <c>slice.len</c>), writable.</summary>
    public ulong Len;

    /// <summary>Construct a const slice over <paramref name="len"/> pointers at <paramref name="ptr"/>.</summary>
    public unsafe ConstPtrSlice(T** ptr, ulong len)
    {
        _ptr = (nint)ptr;
        Len = len;
    }

    /// <summary>Construct from a raw stored pointer value (the <see cref="PtrSlice{T}"/> conversion).</summary>
    internal ConstPtrSlice(nint ptr, ulong len)
    {
        _ptr = ptr;
        Len = len;
    }

    /// <summary>The data pointer (Zig <c>slice.ptr</c>), writable.</summary>
    public unsafe T** Ptr
    {
        readonly get => (T**)_ptr;
        set => _ptr = (nint)value;
    }

    /// <summary>Element access (Zig <c>slice[i]</c>); read-only. Not bounds-checked in this build.</summary>
    public unsafe T* this[ulong i] => ((T**)_ptr)[i];

    /// <summary>The sub-slice <c>slice[lo..hi]</c>.</summary>
    public unsafe ConstPtrSlice<T> Sub(ulong lo, ulong hi) => new((T**)_ptr + lo, hi - lo);
}
