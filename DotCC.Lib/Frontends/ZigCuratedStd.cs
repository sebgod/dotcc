#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Why a curated std entry exists (dotcc #231 rule 1): <see cref="PlatformFloor"/> is what must stay redirected
/// to the BCL for good (OS-backed allocators, stderr output); <see cref="Scaffold"/> is everything else, which a
/// road-to-zig-std G-goal retires by compiling the real std slice from source.</summary>
internal enum CuratedKind
{
    /// <summary>Redirected to the BCL permanently (#231 rule 1's platform floor, S8 #167).</summary>
    PlatformFloor,

    /// <summary>Scaffolding that a G-goal or an open issue removes; counted by <see cref="ZigCuratedStd.ScaffoldCount"/>.</summary>
    Scaffold,
}

/// <summary>The syntactic shape a curated std path is looked up in.</summary>
internal enum CuratedShape
{
    /// <summary>A non-generic type in type position (<c>std.mem.Allocator</c>).</summary>
    Type,

    /// <summary>A generic type spelled as a call with one type argument (<c>std.ArrayList(T)</c>).</summary>
    GenericType,

    /// <summary>A value (<c>std.heap.page_allocator</c>).</summary>
    Value,

    /// <summary>A function of a curated std namespace called as <c>std.mem.eql(…)</c>.</summary>
    NamespaceFn,
}

/// <summary>One row of the curated-std registry (#233): a std path the front end models by hand instead of compiling from
/// source. The row is static data, readable without a <see cref="ZigLowering"/> instance (a std probe report can print it).</summary>
/// <param name="Path">The qualified std path, e.g. <c>std.mem.sliceTo</c>.</param>
/// <param name="Shape">Where the path resolves.</param>
/// <param name="Kind">Platform floor or scaffold.</param>
/// <param name="RetiresWith">The retirement condition (#231 rule 3): the G-goal or issue that removes the row.</param>
/// <param name="Arity">The argument count a <see cref="CuratedShape.NamespaceFn"/> row takes; -1 when the lowering checks it.</param>
/// <param name="ArgShape">The argument list as the arity error spells it, e.g. <c>(type, a, b)</c>.</param>
/// <param name="Lower">The lowering of a namespace function call.</param>
/// <param name="MakeType">The CType factory of a <see cref="CuratedShape.Type"/> row.</param>
/// <param name="MakeGeneric">The CType factory over the lowered element of a <see cref="CuratedShape.GenericType"/> row.</param>
/// <param name="Alloc">The allocator kind of a default-allocator <see cref="CuratedShape.Value"/> row.</param>
/// <param name="BareInStdMem">True when std's own mem.zig spells the function bare (<c>asBytes(x)</c>) and that spelling is curated too.</param>
/// <param name="StdFile">The std file (under the std root) this type row stands for, when std names it by import.</param>
internal sealed record CuratedStdEntry(
    string Path,
    CuratedShape Shape,
    CuratedKind Kind,
    string RetiresWith,
    int Arity = -1,
    string ArgShape = "",
    Func<ZigLowering, IReadOnlyList<Item>, CExpr>? Lower = null,
    Func<CType>? MakeType = null,
    Func<CType, CType>? MakeGeneric = null,
    ZigLowering.AllocKind? Alloc = null,
    string? StdFile = null,
    bool BareInStdMem = false)
{
    /// <summary>The namespace of a function row (<c>std.mem</c> for <c>std.mem.eql</c>).</summary>
    public string Namespace => Path[..Path.LastIndexOf('.')];

    /// <summary>The member name of a function row (<c>eql</c> for <c>std.mem.eql</c>).</summary>
    public string Member => Path[(Path.LastIndexOf('.') + 1)..];
}

/// <summary>The curated-std registry: the burn-down ledger of #231 (rules 2 and 3). Every std path dotcc models by hand has
/// one row here, with the condition that retires it. A unit test fails when a row has none.</summary>
internal static class ZigCuratedStd
{
    private const string MemRetire = "G2 / #172: std.mem compiles from source; the byte views also wait on `@Pointer` / CopyPtrAttrs reification (no issue yet)";
    private const string FloorRetire = "S8 / #167: platform floor, redirected to the BCL for good";

    /// <summary>All rows, in registration order.</summary>
    public static IReadOnlyList<CuratedStdEntry> Rows { get; } =
    [
        new("std.mem.Allocator", CuratedShape.Type, CuratedKind.Scaffold,
            "G2 / #172: the allocator interface compiles from std's mem/Allocator.zig",
            MakeType: static () => new CType.Allocator(), StdFile: "mem/Allocator.zig"),
        new("std.mem.Allocator.VTable", CuratedShape.Type, CuratedKind.Scaffold,
            "G2 / #172: compiles from std's mem/Allocator.zig with the interface",
            MakeType: static () => new CType.Named(ZigLowering.VTableTypeName)),
        new("std.mem.Alignment", CuratedShape.Type, CuratedKind.Scaffold,
            "G2 / #172: compiles from std's mem/Alignment.zig",
            MakeType: static () => new CType.Named(ZigLowering.AlignmentTypeName)),
        new("std.heap.FixedBufferAllocator", CuratedShape.Type, CuratedKind.Scaffold,
            "G2 / #172: compiles from std's heap/FixedBufferAllocator.zig",
            MakeType: static () => new CType.Named(ZigLowering.FbaTypeName)),
        new("std.heap.ArenaAllocator", CuratedShape.Type, CuratedKind.Scaffold,
            "G2 / #172: compiles from std's heap/arena_allocator.zig",
            MakeType: static () => new CType.Named(ZigLowering.ArenaTypeName)),
        new("std.ArrayList", CuratedShape.GenericType, CuratedKind.Scaffold,
            "G4 / #172: std.ArrayList compiles from source; may stay a fast path only with a differential test against it",
            MakeGeneric: static elem => new CType.ZigList(elem)),
        new("std.heap.page_allocator", CuratedShape.Value, CuratedKind.PlatformFloor, FloorRetire,
            Alloc: ZigLowering.AllocKind.CHeap),
        new("std.heap.c_allocator", CuratedShape.Value, CuratedKind.PlatformFloor, FloorRetire,
            Alloc: ZigLowering.AllocKind.CHeap),

        new("std.mem.eql", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            3, "(type, a, b)", static (z, a) => z.LowerMemEql(a)),
        new("std.mem.copyForwards", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            3, "(type, dest, source)", static (z, a) => z.LowerMemCopyForwards(a)),
        new("std.mem.span", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            1, "(pointer)", static (z, a) => z.LowerMemSpan(a)),
        new("std.mem.zeroes", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            1, "(type)", static (z, a) => z.LowerMemZeroes(a)),
        new("std.mem.asBytes", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            1, "(ptr)", static (z, a) => z.LowerMemAsBytes(a), BareInStdMem: true),
        new("std.mem.sliceAsBytes", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            1, "(slice)", static (z, a) => z.LowerMemSliceAsBytes(a), BareInStdMem: true),
        new("std.mem.bytesAsValue", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            2, "(type, bytes)", static (z, a) => z.LowerMemBytesAsValue(a, "bytesAsValue"), BareInStdMem: true),
        new("std.mem.bytesToValue", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            2, "(type, bytes)", static (z, a) => z.LowerMemBytesAsValue(a, "bytesToValue"), BareInStdMem: true),
        new("std.mem.bytesAsSlice", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            2, "(type, bytes)", static (z, a) => z.LowerMemBytesAsSlice(a), BareInStdMem: true),
        new("std.mem.sliceTo", CuratedShape.NamespaceFn, CuratedKind.Scaffold, MemRetire,
            2, "(ptr, end)", static (z, a) => z.LowerMemSliceTo(a), BareInStdMem: true),

        new("std.debug.print", CuratedShape.NamespaceFn, CuratedKind.PlatformFloor, FloorRetire,
            2, "(format, .{args})", static (z, a) => z.LowerDebugPrint(a)),

        new("std.testing.expect", CuratedShape.NamespaceFn, CuratedKind.Scaffold,
            "G3 / #172: std.testing compiles from source over the real std.fmt",
            1, "(ok: bool)", static (z, a) => z.LowerTestingExpect(a)),
        new("std.testing.expectEqual", CuratedShape.NamespaceFn, CuratedKind.Scaffold,
            "G3 / #172: std.testing compiles from source over the real std.fmt",
            2, "(expected, actual)", static (z, a) => z.LowerTestingExpectEqual(a)),
        new("std.testing.expectError", CuratedShape.NamespaceFn, CuratedKind.Scaffold,
            "G3 / #172: std.testing compiles from source over the real std.fmt",
            2, "(expected_error, actual)", static (z, a) => z.LowerTestingExpectError(a)),
        new("std.testing.expectEqualStrings", CuratedShape.NamespaceFn, CuratedKind.Scaffold,
            "G3 / #172: std.testing compiles from source over the real std.fmt",
            2, "(expected, actual)", static (z, a) => z.LowerTestingExpectEqualStrings(a)),
        new("std.testing.expectEqualSlices", CuratedShape.NamespaceFn, CuratedKind.Scaffold,
            "G3 / #172: std.testing compiles from source over the real std.fmt",
            3, "(T, expected, actual)", static (z, a) => z.LowerTestingExpectEqualSlices(a)),

        new("std.fmt.comptimePrint", CuratedShape.NamespaceFn, CuratedKind.Scaffold,
            "G3 / #172: std.fmt runs at comptime from source (needs the comptime evaluator of #232)",
            2, "(format, argument tuple)", static (z, a) => z.LowerComptimePrint(a)),
    ];

    private static readonly Dictionary<string, CuratedStdEntry> ByPath = Rows.ToDictionary(r => r.Path, StringComparer.Ordinal);

    /// <summary>The number of rows that are scaffolding: the burn-down count of #231 rule 2.</summary>
    public static int ScaffoldCount { get; } = Rows.Count(r => r.Kind == CuratedKind.Scaffold);

    /// <summary>The row for <paramref name="path"/>, or null.</summary>
    public static CuratedStdEntry? Find(string path) => ByPath.GetValueOrDefault(path);

    /// <summary>The row for <paramref name="path"/> when it has shape <paramref name="shape"/>, or null.</summary>
    public static CuratedStdEntry? Find(string path, CuratedShape shape)
        => ByPath.TryGetValue(path, out var row) && row.Shape == shape ? row : null;

    /// <summary>True when <paramref name="path"/> is a type, generic type or value row: a path the curated model owns.</summary>
    public static bool IsOwnedPath(string path)
        => ByPath.TryGetValue(path, out var row) && row.Shape != CuratedShape.NamespaceFn;

    /// <summary>The paths of every row of <paramref name="shape"/>, in order (for "not modeled" messages).</summary>
    public static IEnumerable<string> Paths(CuratedShape shape) => Rows.Where(r => r.Shape == shape).Select(r => r.Path);

    /// <summary>The function row <c>ns.member</c>, or null.</summary>
    public static CuratedStdEntry? FindFunction(string ns, string member) => Find(ns + "." + member, CuratedShape.NamespaceFn);

    /// <summary>The member names of the function rows under <paramref name="ns"/>.</summary>
    public static IEnumerable<string> Members(string ns)
        => Rows.Where(r => r.Shape == CuratedShape.NamespaceFn && r.Namespace == ns).Select(r => r.Member);

    /// <summary>The curated type a std file stands for (<c>mem/Allocator.zig</c>), or null.</summary>
    public static CuratedStdEntry? FindByFile(string relativePath)
        => Rows.FirstOrDefault(r => r.StdFile is { } f && string.Equals(f, relativePath, StringComparison.Ordinal));

    /// <summary>True when <paramref name="ns"/> is a curated function namespace.</summary>
    public static bool IsNamespace(string ns) => Rows.Any(r => r.Shape == CuratedShape.NamespaceFn && r.Namespace == ns);
}
