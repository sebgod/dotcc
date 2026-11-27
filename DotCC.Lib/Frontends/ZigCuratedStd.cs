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

    /// <summary>A static function of a curated type, called through the type (<c>std.heap.ArenaAllocator.init(a)</c>).</summary>
    StaticFn,

    /// <summary>A method of a curated runtime value, called on a receiver (<c>list.append(a, x)</c>).</summary>
    Method,

    /// <summary>A field of a curated runtime value (<c>list.items</c>).</summary>
    Field,

    /// <summary>A decl literal at a curated type's sink (<c>.empty</c> at a <c>std.ArrayList(T)</c>).</summary>
    DeclLiteral,
}

/// <summary>What a member row's lowering is given: the call as the front end sees it (#233). The delegates of
/// <see cref="CuratedShape.StaticFn"/>, <see cref="CuratedShape.Method"/>, <see cref="CuratedShape.Field"/> and
/// <see cref="CuratedShape.DeclLiteral"/> rows take it, so no row reaches back into the call site.</summary>
/// <param name="Member">The member name as spelled (<c>append</c>, <c>items</c>, <c>empty</c>).</param>
/// <param name="Args">The argument items of a call (empty for a field or a decl literal).</param>
/// <param name="Recv">The lowered receiver of a method or field; for an allocator method, null when the receiver is a devirtualised default.</param>
/// <param name="Base">The receiver's source item (the allocator facts and the FBA site table are keyed by it).</param>
/// <param name="Kind">The allocator devirtualisation kind TryKnownAllocatorKind found, null when the receiver is opaque.</param>
/// <param name="FbaCtx">The <c>&amp;fba</c> context of a provable <c>fba.allocator()</c> site.</param>
/// <param name="Arrow">True when a field's receiver is a pointer (zig auto-dereferences).</param>
/// <param name="Sink">The result type a decl literal is written at.</param>
internal sealed record CuratedMemberCall(
    string Member,
    IReadOnlyList<Item> Args,
    CExpr? Recv = null,
    Item? Base = null,
    ZigLowering.AllocKind? Kind = null,
    CExpr? FbaCtx = null,
    bool Arrow = false,
    CType? Sink = null);

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
/// <param name="LowerMember">The lowering of a static function, method, field or decl literal row.</param>
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
    bool BareInStdMem = false,
    Func<ZigLowering, CuratedMemberCall, CExpr>? LowerMember = null)
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

        // std.ArrayList(T): the unmanaged API's members, keyed under the generic type's path.
        Member("std.ArrayList.append", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListAppend(c), "(alloc, item)"),
        Member("std.ArrayList.appendSlice", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListAppendSlice(c), "(alloc, slice)"),
        Member("std.ArrayList.pop", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListPop(c), "()"),
        Member("std.ArrayList.deinit", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListDeinit(c), "(alloc)"),
        Member("std.ArrayList.clearRetainingCapacity", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListClearRetainingCapacity(c), "()"),
        Member("std.ArrayList.insert", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListInsert(c), "(alloc, index, item)"),
        Member("std.ArrayList.insertSlice", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListInsertSlice(c), "(alloc, index, slice)"),
        Member("std.ArrayList.appendNTimes", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListAppendNTimes(c, assume: false), "(alloc, value, n)"),
        Member("std.ArrayList.appendNTimesAssumeCapacity", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListAppendNTimes(c, assume: true), "(value, n)"),
        Member("std.ArrayList.replaceRange", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListReplaceRange(c, assume: false), "(alloc, start, len, new_items)"),
        Member("std.ArrayList.replaceRangeAssumeCapacity", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListReplaceRange(c, assume: true), "(start, len, new_items)"),
        Member("std.ArrayList.orderedRemove", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListRemove(c, ordered: true), "(index)"),
        Member("std.ArrayList.swapRemove", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListRemove(c, ordered: false), "(index)"),
        Member("std.ArrayList.getLast", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListGetLast(c), "()"),
        Member("std.ArrayList.ensureTotalCapacity", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListEnsureCapacity(c, total: true), "(alloc, n)"),
        Member("std.ArrayList.ensureUnusedCapacity", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListEnsureCapacity(c, total: false), "(alloc, n)"),
        Member("std.ArrayList.appendAssumeCapacity", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListAppendAssumeCapacity(c), "(item)"),
        Member("std.ArrayList.shrinkRetainingCapacity", CuratedShape.Method, ListRetire, static (z, c) => z.LowerListShrinkRetainingCapacity(c), "(n)"),
        Member("std.ArrayList.items", CuratedShape.Field, ListRetire, static (z, c) => z.LowerListItems(c)),
        Member("std.ArrayList.capacity", CuratedShape.Field, ListRetire, static (z, c) => z.LowerListCapacity(c)),
        Member("std.ArrayList.empty", CuratedShape.DeclLiteral, ListRetire, static (z, c) => z.LowerListEmpty(c)),

        // std.mem.Allocator: the methods, devirtualised over a provable receiver or dispatched through the vtable.
        Member("std.mem.Allocator.alloc", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorAlloc(c, aligned: false)),
        Member("std.mem.Allocator.alignedAlloc", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorAlloc(c, aligned: true)),
        Member("std.mem.Allocator.dupe", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorDupe(c)),
        Member("std.mem.Allocator.dupeSentinel", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorSentinel(c, dupe: true)),
        Member("std.mem.Allocator.allocSentinel", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorSentinel(c, dupe: false)),
        Member("std.mem.Allocator.free", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorFree(c)),
        Member("std.mem.Allocator.create", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorCreate(c)),
        Member("std.mem.Allocator.destroy", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorDestroy(c)),
        Member("std.mem.Allocator.realloc", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorRealloc(c)),
        Member("std.mem.Allocator.resize", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorResize(c, remap: false)),
        Member("std.mem.Allocator.remap", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorResize(c, remap: true)),
        Member("std.mem.Allocator.rawAlloc", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorRaw(c, "rawAlloc")),
        Member("std.mem.Allocator.rawResize", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorRaw(c, "rawResize")),
        Member("std.mem.Allocator.rawRemap", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorRaw(c, "rawRemap")),
        Member("std.mem.Allocator.rawFree", CuratedShape.Method, AllocRetire, static (z, c) => z.LowerAllocatorRaw(c, "rawFree")),

        // The two runtime allocators.
        Member("std.heap.FixedBufferAllocator.init", CuratedShape.StaticFn, FbaRetire, static (z, c) => z.LowerFbaInit(c.Args), "(buffer)"),
        Member("std.heap.FixedBufferAllocator.allocator", CuratedShape.Method, FbaRetire, static (z, c) => z.LowerRuntimeAllocatorAllocator(c), "()"),
        Member("std.heap.ArenaAllocator.init", CuratedShape.StaticFn, ArenaRetire, static (z, c) => z.LowerArenaInit(c.Args), "(backing allocator)"),
        Member("std.heap.ArenaAllocator.allocator", CuratedShape.Method, ArenaRetire, static (z, c) => z.LowerRuntimeAllocatorAllocator(c), "()"),
        Member("std.heap.ArenaAllocator.deinit", CuratedShape.Method, ArenaRetire, static (z, c) => z.LowerArenaDeinit(c), "()"),

        // std.mem.Alignment: the carrier's statics and methods.
        Member("std.mem.Alignment.toByteUnits", CuratedShape.Method, AlignRetire, static (z, c) => z.LowerAlignmentMethod(c, "Alignment.ToByteUnits", CType.ULong, 0), "()"),
        Member("std.mem.Alignment.forward", CuratedShape.Method, AlignRetire, static (z, c) => z.LowerAlignmentMethod(c, "Alignment.Forward", CType.ULong, 1), "(addr)"),
        Member("std.mem.Alignment.backward", CuratedShape.Method, AlignRetire, static (z, c) => z.LowerAlignmentMethod(c, "Alignment.Backward", CType.ULong, 1), "(addr)"),
        Member("std.mem.Alignment.check", CuratedShape.Method, AlignRetire, static (z, c) => z.LowerAlignmentMethod(c, "Alignment.Check", CType.Bool, 1), "(addr)"),
        Member("std.mem.Alignment.fromByteUnits", CuratedShape.StaticFn, AlignRetire, static (z, c) => z.LowerAlignmentFromByteUnits(c), "(n)"),
        Member("std.mem.Alignment.of", CuratedShape.StaticFn, AlignRetire, static (z, c) => z.LowerAlignmentOf(c), "(T)"),
    ];

    private const string ListRetire = "G4 / #172: std.ArrayList compiles from source; its members go with the curated type";
    private const string AllocRetire = "G2 / #172: the allocator interface compiles from std's mem/Allocator.zig; its methods go with the curated type";
    private const string FbaRetire = "G2 / #172: compiles from std's heap/FixedBufferAllocator.zig";
    private const string ArenaRetire = "G2 / #172: compiles from std's heap/arena_allocator.zig";
    private const string AlignRetire = "G2 / #172: compiles from std's mem/Alignment.zig";

    /// <summary>A member row (static function, method, field or decl literal) of a curated type.</summary>
    private static CuratedStdEntry Member(string path, CuratedShape shape, string retiresWith,
        Func<ZigLowering, CuratedMemberCall, CExpr> lower, string argShape = "")
        => new(path, shape, CuratedKind.Scaffold, retiresWith, ArgShape: argShape, LowerMember: lower);

    /// <summary>The curated type path of a lowered type (<c>std.ArrayList</c> for a <see cref="CType.ZigList"/>), or null
    /// when the type is not a curated runtime type. Found through the type rows' own factories, so no name is repeated.</summary>
    public static string? PathOfType(CType type) => type.Unqualified switch
    {
        CType.ZigList => ListPath,
        CType.Allocator => AllocatorPath,
        CType.Named named => NamedPaths.GetValueOrDefault(named.Name),
        _ => null,
    };

    private static readonly string? ListPath =
        Rows.FirstOrDefault(r => r.Shape == CuratedShape.GenericType && r.MakeGeneric?.Invoke(CType.Void) is CType.ZigList)?.Path;

    /// <summary>The path of the curated <c>std.mem.Allocator</c> type row, the owner of the allocator method rows.</summary>
    public static readonly string? AllocatorPath =
        Rows.FirstOrDefault(r => r.Shape == CuratedShape.Type && r.MakeType?.Invoke() is CType.Allocator)?.Path;

    private static readonly Dictionary<string, string> NamedPaths = BuildNamedPaths();

    /// <summary>The curated struct-typed rows by the name their type lowers to (<c>FixedBufferAllocator</c>).</summary>
    private static Dictionary<string, string> BuildNamedPaths()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (row.Shape == CuratedShape.Type && row.MakeType?.Invoke() is CType.Named named) { map[named.Name] = row.Path; }
        }
        return map;
    }

    /// <summary>The member row <paramref name="member"/> hanging under the curated type <paramref name="typePath"/>, or null.</summary>
    public static CuratedStdEntry? FindMember(string typePath, string member)
        => Rows.FirstOrDefault(r => r.LowerMember is not null && r.Namespace == typePath
            && string.Equals(r.Member, member, StringComparison.Ordinal));

    /// <summary>The member row <paramref name="member"/> under <paramref name="typePath"/> when it has shape <paramref name="shape"/>.</summary>
    public static CuratedStdEntry? FindMember(string typePath, string member, CuratedShape shape)
        => FindMember(typePath, member) is { } row && row.Shape == shape ? row : null;

    /// <summary>The rows of <paramref name="shape"/> under the curated type <paramref name="typePath"/>, in order.</summary>
    public static IEnumerable<CuratedStdEntry> MembersOf(string typePath, CuratedShape shape)
        => Rows.Where(r => r.Shape == shape && r.LowerMember is not null && r.Namespace == typePath);

    /// <summary>True when an unknown member of the curated type <paramref name="typePath"/> is a clear "no modeled member"
    /// error (the list and the Alignment carrier); the runtime allocators' other members continue to the generic dispatch.</summary>
    public static bool ReportsUnmodeledMembers(string typePath)
        => typePath == ListPath || typePath == NamedPaths.GetValueOrDefault(ZigLowering.AlignmentTypeName);

    /// <summary>The error for member <paramref name="member"/> of the curated type <paramref name="typePath"/> that has no row
    /// (or that a call gave the wrong argument count: <paramref name="argCount"/> &gt;= 0), listing the curated set from the rows.</summary>
    public static IrUnsupportedException UnmodeledMember(string typePath, string member, int argCount = -1)
    {
        var taking = argCount >= 0 ? $" taking {argCount} argument(s)" : "";
        var curated = typePath == ListPath
            ? string.Join(", ", MembersOf(typePath, CuratedShape.Method).Concat(MembersOf(typePath, CuratedShape.Field)).Select(r => r.Member))
            : string.Join(", ", MembersOf(typePath, CuratedShape.Method).Concat(MembersOf(typePath, CuratedShape.StaticFn))
                .Select(r => r.Member + r.ArgShape));
        return new IrUnsupportedException($"zig {typePath} has no modeled member '{member}'{taking} (curated: {curated})");
    }

    /// <summary>The curated type paths that have a static function row named <paramref name="member"/>.</summary>
    public static IEnumerable<string> TypesWithStatic(string member)
        => Rows.Where(r => r.Shape == CuratedShape.StaticFn && r.Member == member).Select(r => r.Namespace);

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
        => ByPath.TryGetValue(path, out var row) && row.Shape is CuratedShape.Type or CuratedShape.GenericType or CuratedShape.Value;

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
