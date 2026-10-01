#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotCC.Libc;

// File-scope ("global") C array storage. A C file-scope array — `int t[8];`,
// `const char s[] = "..."`, `static const lu_byte tab[N] = {...}` — has static
// storage duration (it lives for the whole program) and decays to a pointer.
// dotcc lowers it to a `T*` field in DotCcGlobals that points into a managed
// array allocated on the Pinned Object Heap, so ordinary unsafe pointer
// arithmetic / subscripting works EXACTLY as it does for a block-scope
// `stackalloc` array — same `T*` shape, just program-lifetime instead of
// frame-lifetime. The pinned array must also stay rooted (pinned != rooted: a
// POH object is still collectible if nothing references it), so each one is
// held in a static list for the program's life.
public static unsafe partial class Libc
{
    private static readonly List<object> _globalArrayRoots = new();

    private static T* PinAndRoot<T>(T[] arr) where T : unmanaged
    {
        lock (_globalArrayRoots) { _globalArrayRoots.Add(arr); }
        // The array is on the Pinned Object Heap (GC.AllocateArray(pinned:true)),
        // so its data address is stable for the program lifetime.
        return (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(arr));
    }

    /// <summary>
    /// Allocate a pinned, zero-initialized global array of <paramref name="length"/>
    /// elements and return a stable <c>T*</c> into it. Lowering for a file-scope
    /// <c>T a[N];</c> (no initializer — C zero-initializes static storage).
    /// </summary>
    public static T* GlobalArrayZeroed<T>(int length) where T : unmanaged =>
        PinAndRoot(GC.AllocateArray<T>(length, pinned: true));

    /// <summary>
    /// Allocate a pinned global array initialized from <paramref name="init"/> and
    /// return a stable <c>T*</c> into it. Lowering for <c>T a[N] = { … }</c> /
    /// <c>T a[] = { … }</c> / <c>char s[] = "…"</c>; the emitted code passes the
    /// already-flattened element values as a <c>new T[]{ … }</c> literal.
    /// </summary>
    public static T* GlobalArrayFrom<T>(ReadOnlySpan<T> init) where T : unmanaged
    {
        var arr = GC.AllocateUninitializedArray<T>(init.Length, pinned: true);
        init.CopyTo(arr);
        return PinAndRoot(arr);
    }

    /// <summary>
    /// Store <paramref name="init"/> into the global array storage at
    /// <paramref name="dst"/>, allocated earlier by <see cref="GlobalArrayZeroed{T}"/>:
    /// the second half of a file-scope array whose initializer takes other objects'
    /// addresses, run once all static storage exists. Returns true, for the field
    /// initializer that runs it.
    /// </summary>
    public static bool GlobalArrayFill<T>(T* dst, ReadOnlySpan<T> init) where T : unmanaged
    {
        init.CopyTo(new Span<T>(dst, init.Length));
        return true;
    }

    /// <summary>
    /// The storage of a file-scope array of function pointers, initialised from
    /// <paramref name="arr"/>: a <c>delegate*</c> can't be a generic argument, so the emitter
    /// builds the initializer as an array and this copies it into native memory of the same
    /// size, one function pointer per element, which lives for the program. The caller casts
    /// the result to the element pointer type. Copied rather than pinned: Mono (the browser's
    /// .NET runtime) refuses to pin an array whose element type is a function pointer.
    /// </summary>
    public static unsafe void* PinFnPtrArray(Array arr)
    {
        var bytes = (nuint)arr.Length * (nuint)sizeof(nint);
        var storage = NativeMemory.AllocZeroed(bytes == 0 ? 1 : bytes);
        if (bytes != 0)
        {
            fixed (byte* src = &MemoryMarshal.GetArrayDataReference(arr))
            {
                Buffer.MemoryCopy(src, storage, bytes, bytes);
            }
        }
        return storage;
    }

    /// <summary>
    /// The base an <c>offsetof</c> measures from: <c>&amp;((T*)OffsetOfBase)-&gt;m</c> minus this
    /// address is the member's offset, and no member is ever read. It is real memory, not a
    /// made-up address: C#'s <c>-&gt;</c> null-checks its base, and CoreCLR checks by reading a
    /// byte there (a null base faults, and so does an unmapped one).
    /// </summary>
    public static readonly void* OffsetOfBase = NativeMemory.AllocZeroed(64);
}
