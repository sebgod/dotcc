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

    // Pinned delegate* arrays + their GCHandles (delegate* can't be a generic arg).
    private static readonly List<(GCHandle Handle, Array Arr)> _fnPtrArrays = new();

    /// <summary>
    /// Pin a delegate*-element array and return a stable void* to element 0.
    /// The caller casts the result to the appropriate pointer type. The array
    /// and its GCHandle are rooted for program lifetime.
    /// </summary>
    public static unsafe void* PinFnPtrArray(Array arr)
    {
        var handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
        lock (_fnPtrArrays) { _fnPtrArrays.Add((handle, arr)); }
        return (void*)handle.AddrOfPinnedObject();
    }
}
