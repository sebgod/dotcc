#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace DotCC.Ir;

/// <summary>The FRONT-END-NEUTRAL typed IR module — what every front-end builds into and every backend
/// reads. It owns the program's output (<see cref="Functions"/>, <see cref="Globals"/>, the emitted
/// aggregate and enum definitions, <see cref="Diagnostics"/>, the Zig test manifest and error-code table),
/// the aggregate / enum REGISTRIES with their compile-time layout model (<see cref="SizeOfConst"/>,
/// <see cref="AlignOfConst"/>, <see cref="OffsetOfConstPath"/>, field types), and the comptime
/// interpreter (<see cref="ConstEval"/>, in <c>IrModule.Comptime.cs</c>).
/// <para>It knows nothing of any source language. The C front-end's binder is <see cref="IrBuilder"/>,
/// which builds into one of these (<see cref="IrBuilder.Module"/>); the Zig front-end's is
/// <c>ZigLowering</c>, a peer that holds one directly. Until the 2026-09 architecture review these were
/// one class — the C binder and the neutral IR were the same <c>IrBuilder</c>, so a second front-end
/// reached the IR through "internal shims" into the C binder's own tables.</para></summary>
internal sealed partial class IrModule
{
    // Struct/union name → its fields, so member access can resolve a field's
    // type. Keyed by the canonical (tag, or anonymous-typedef alias) name.
    internal Dictionary<string, List<StructField>> StructFields { get; } = new(StringComparer.Ordinal);
    // Whether each registered aggregate is a union — drives the compile-time layout
    // model (offsetof folding) and is set alongside every StructFields entry.
    internal Dictionary<string, bool> StructIsUnion { get; } = new(StringComparer.Ordinal);
    // Byte-packed structs (Zig `packed struct`): the compile-time layout model drops
    // inter-field padding + aligns to 1 for these, so `@sizeOf`/`offsetof` match the
    // emitted [StructLayout(Sequential, Pack=1)] runtime layout. (Zig front-end only.)
    internal HashSet<string> PackedStructs { get; } = new(StringComparer.Ordinal);
    // Enum tag → its resolved CType.Enum, so `enum Tag` as a type resolves to the
    // real enum (not plain int). Anonymous-but-typedef'd enums are reached through
    // _typedefs instead (the alias maps to the same CType.Enum).
    internal Dictionary<string, CType.Enum> EnumTypes { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> EmittedTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>True when this module is one translation unit compiled to an object
    /// (<c>--emit=obj</c>). Its types are shared with every unit it links with, so each
    /// renders in full rather than trimmed to what this unit's own code uses.</summary>
    public bool IsObject { get; set; }

    /// <summary>True when a unit included dotcc's synthetic <c>&lt;Python.h&gt;</c>: the program
    /// links the runtime's abi3 shim (<c>PythonLib</c>). CPython itself, whose own headers
    /// shadow the synthetic one, defines those names and gets no shim.</summary>
    public bool UsesPythonShim { get; set; }

    /// <summary>The struct and union tags the program's own sources name. One never completed is
    /// only ever pointed at (C11 6.7.2.3: an incomplete type), and the backend gives it an empty
    /// placeholder so the pointers have a type, unless it is in <see cref="RuntimeTags"/>.</summary>
    public HashSet<string> DeclaredTags { get; } = new(StringComparer.Ordinal);

    /// <summary>The tags a synthetic header names: <c>struct tm</c>, <c>struct stat</c> and the
    /// like are body-less there because the runtime supplies them as C# types.</summary>
    public HashSet<string> RuntimeTags { get; } = new(StringComparer.Ordinal);

    /// <summary>The functions a library unit defined (see <see cref="IrBuilder.AddUnit(Item, string, bool)"/>):
    /// the program defines none of them itself, so a backend may still lower a call to one
    /// its own way (the wat target expands a <c>printf</c> with a literal format inline) and
    /// keep the library's for the calls it cannot.</summary>
    public HashSet<string> LibraryFunctions { get; } = new(StringComparer.Ordinal);

    /// <summary>The file-scope objects (and function-scope statics) a library unit defined: a
    /// backend may leave out one nothing it emits reaches, as it does a library function.</summary>
    public HashSet<Symbol> LibraryGlobals { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The functions defined <c>[[gnu::constructor]]</c>, in the order they were defined:
    /// the program runs them before <c>main</c>, those with the lower
    /// <see cref="Symbol.ConstructorPriority"/> first (see <see cref="ConstructorsInOrder"/>).</summary>
    public List<Symbol> Constructors { get; } = new();

    /// <summary><see cref="Constructors"/> in the order they run: by priority, and in definition
    /// order among equals.</summary>
    public IEnumerable<Symbol> ConstructorsInOrder => Constructors.OrderBy(s => s.ConstructorPriority ?? DefaultConstructorPriority);

    /// <summary>The priority of a constructor that gives none (GCC's: after every one that does).</summary>
    public const int DefaultConstructorPriority = 65536;

    public List<FuncDef> Functions { get; } = new();
    public List<GlobalVar> Globals { get; } = new();
    public List<StructTypeDef> Types { get; } = new();
    public List<EnumTypeDef> Enums { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();

    /// <summary>Zig test-mode manifest: each <c>test "name" {}</c> block, lowered to a runnable
    /// <c>anyerror!void</c> function that is recorded in <see cref="Functions"/> like any other,
    /// paired with its display name (in source order). Populated ONLY when the Zig front-end runs
    /// in test mode (<c>dotcc zig test</c>); empty otherwise. The backend hands it to the shell so
    /// the emitted program's entry point runs each test and reports pass/fail instead of calling
    /// <c>main</c>.</summary>
    public List<(string Name, Symbol Sym)> Tests { get; } = new();

    /// <summary>The Zig front-end's flat error set: each <c>error.Foo</c> name → its stable
    /// <c>ushort</c> code (1-based). Populated by <c>ZigFrontend.AddUnits</c> after lowering all
    /// units; consumed by the backend to emit the <c>__zigErrorName</c> code→name table that
    /// backs <c>@errorName</c> (Milestone X, part 1). Null/empty for a C-only program or a Zig
    /// program that never names an error.</summary>
    public IReadOnlyDictionary<string, int>? ZigErrorCodes { get; set; }

    /// <summary>C import-mode analysis results (<c>-l</c>): prototype-only functions a native library must
    /// resolve, and extern DATA referenced but defined nowhere. Computed by the C front-end from its own
    /// binding state and published here (<see cref="IrBuilder.PublishImportAnalysis"/>) so the emit pass
    /// reads them off the module; empty for a Zig-only program.</summary>
    public IReadOnlyDictionary<string, Symbol> ProtoOnlyReferenced { get; internal set; } = new Dictionary<string, Symbol>(StringComparer.Ordinal);

    /// <summary>See <see cref="ProtoOnlyReferenced"/>.</summary>
    public IReadOnlyList<string> ExternDataReferenced { get; internal set; } = Array.Empty<string>();

    // ---- the compile-time layout model -------------------------------------

    /// <summary>The constant byte size of a type — the layout model for a user
    /// aggregate (so the size is exact for an array bound), else the type's own
    /// <see cref="CType.SizeOf"/>.</summary>
    internal long? SizeOfConst(CType t) =>
        t.Unqualified is CType.Named n && StructFields.ContainsKey(n.Name) ? Layout(t).Size : t.SizeOf;

    // ---- compile-time C-ABI layout (for offsetof / sizeof folding) --------
    // The .NET blittable layout dotcc emits (sequential structs, explicit unions,
    // natural alignment on this LP64 target) matches the C ABI for the types it
    // models, so size/offset can be computed at compile time — which an array bound
    // like `char padding[offsetof(T, m)]` requires (Lua's alignment-union trick).

    /// <summary>The ABI alignment (in bytes) of a type — the comptime value of Zig's
    /// <c>@alignOf(T)</c> (Milestone T, part 4). A pure compile-time constant on this LP64 target, so
    /// the Zig front-end folds it straight to a literal; surfaced here because <see cref="Layout"/> is
    /// private and the layout model (natural alignment, struct = max field alignment) lives in this
    /// type.</summary>
    internal int AlignOfConst(CType t) => Layout(t).Align;

    /// <summary>The (size, alignment) in bytes of a type under the C ABI / .NET
    /// blittable layout.</summary>
    private (int Size, int Align) Layout(CType t)
    {
        switch (t.Unqualified)
        {
            case CType.Prim p: return (p.Bytes, p.Bytes);
            case CType.Pointer or CType.Func: return (8, 8);
            case CType.Array a:
            {
                var (es, ea) = Layout(a.FlatElement);
                var count = 1;
                for (CType c = a; c is CType.Array ca; c = ca.Element) { count *= ca.Count ?? 0; }
                return (es * count, ea);
            }
            case CType.Named n: return LayoutAggregate(n.Name);
            // An enum is laid out as its underlying integer (a C# enum field is).
            case CType.Enum e: return Layout(e.Underlying);
            // _Float128 wraps a UInt128, which .NET aligns to 16 on x64, as gcc does.
            case CType.Float128Type: return (16, 16);
            // double _Complex: two doubles, the real part first (C11 6.2.5p13).
            case CType.ComplexType: return (16, 8);
            default: return (0, 1);
        }
    }

    /// <summary>The (size, alignment) of a registered struct/union: sequential
    /// fields each aligned up to their own alignment for a struct; all overlaid at 0
    /// for a union. The total rounds up to the aggregate's alignment.</summary>
    private (int Size, int Align) LayoutAggregate(string name)
    {
        if (!StructFields.TryGetValue(name, out var fields)) { return (0, 1); } // opaque/unknown
        var isUnion = StructIsUnion.GetValueOrDefault(name);
        var packed = PackedStructs.Contains(name);   // byte-packed: no inter-field padding, align 1
        int align = 1, size = 0, off = 0;
        // Consecutive bit-fields share a storage unit of their type's size while they fit, as the C# backend packs them
        // (CSharpBackend.PackBitFieldRun): zig's `packed struct(u8) { a: bool, … }` is ONE byte (task #156), not one per
        // field, and so is C's `unsigned a : 3, b : 5;` unit.
        int unitBytes = -1, unitUsed = 0;
        foreach (var f in fields)
        {
            var (fs, fa) = Layout(f.Type);
            if (!isUnion && f.BitWidth is { } width)
            {
                if (width == 0) { unitBytes = -1; continue; }   // a zero-width field closes the unit
                if (unitBytes == fs && unitUsed + width <= fs * 8) { unitUsed += width; continue; }
                unitBytes = fs;
                unitUsed = width;
            }
            else
            {
                unitBytes = -1;
            }
            if (packed) { fa = 1; }
            if (fa > align) { align = fa; }
            if (isUnion) { if (fs > size) { size = fs; } }
            else { off = RoundUp(off, fa) + fs; }
        }
        return (RoundUp(isUnion ? size : off, align), align);
    }

    /// <summary>The byte offset of a (possibly nested) member-designator within
    /// struct <paramref name="structName"/> — per-level offsets summed, each
    /// intermediate level resolved through its member's type. Null if any level
    /// isn't modelled.</summary>
    internal int? OffsetOfConstPath(string structName, IReadOnlyList<string> path)
    {
        var total = 0;
        var current = structName;
        for (var i = 0; i < path.Count; i++)
        {
            var seg = path[i];
            if (OffsetOfConst(current, seg) is not { } off
                || !StructFields.TryGetValue(current, out var fields)) { return null; }
            total += off;
            if (i == path.Count - 1) { break; }   // final segment — no deeper level to name
            CType? segType = null;
            foreach (var f in fields)
            {
                if (f.Name == seg) { segType = f.Type; break; }
            }
            if ((segType?.Unqualified as CType.Named)?.Name is not { } next) { return null; }
            current = next;
        }
        return total;
    }

    /// <summary>The byte offset of <paramref name="member"/> within struct
    /// <paramref name="structName"/> (0 for any union member), or null if unknown. For a
    /// bit-field, the offset of its storage unit.</summary>
    internal int? OffsetOfConst(string structName, string member) =>
        FieldPlaceOf(structName, member)?.Offset;

    /// <summary>Where one field of a struct or union lives in the layout model: the byte
    /// <see cref="Offset"/> of the field, or, for a bit-field, of the storage unit it shares
    /// (<see cref="UnitBytes"/> wide, its declared type's size), with its lowest bit at
    /// <see cref="BitOffset"/> within the unit, the first field of a unit taking the low bits.</summary>
    internal readonly record struct FieldPlace(StructField Field, int Offset, int BitOffset, int UnitBytes);

    /// <summary>The place of <paramref name="member"/> in <paramref name="structName"/>, or
    /// null when either is unknown.</summary>
    internal FieldPlace? FieldPlaceOf(string structName, string member)
    {
        if (FieldPlaces(structName) is not { } places) { return null; }
        foreach (var p in places)
        {
            if (p.Field.Name == member) { return p; }
        }
        return null;
    }

    /// <summary>Every field's place in <paramref name="structName"/>, laid out as
    /// <see cref="LayoutAggregate"/> sizes it: a struct's fields in order, each aligned (none
    /// when packed), consecutive bit-fields sharing a unit of their type's size while they
    /// fit and a zero-width one closing it; a union's every field at 0. Null when unknown.</summary>
    internal IReadOnlyList<FieldPlace>? FieldPlaces(string structName)
    {
        if (!StructFields.TryGetValue(structName, out var fields)) { return null; }
        var isUnion = StructIsUnion.GetValueOrDefault(structName);
        var packed = PackedStructs.Contains(structName);   // byte-packed: no inter-field padding
        var places = new List<FieldPlace>(fields.Count);
        int off = 0, unitOff = 0, unitBytes = -1, unitUsed = 0;
        foreach (var f in fields)
        {
            var (fs, fa) = Layout(f.Type);
            if (packed) { fa = 1; }
            if (isUnion)
            {
                places.Add(new FieldPlace(f, 0, 0, f.IsBitField ? fs : 0));
                continue;
            }
            if (f.BitWidth is { } width)
            {
                if (width == 0)
                {
                    unitBytes = -1;
                    places.Add(new FieldPlace(f, off, 0, 0));
                    continue;
                }
                if (unitBytes == fs && unitUsed + width <= fs * 8)
                {
                    places.Add(new FieldPlace(f, unitOff, unitUsed, fs));
                    unitUsed += width;
                    continue;
                }
                off = RoundUp(off, fa);
                unitOff = off;
                unitBytes = fs;
                unitUsed = width;
                places.Add(new FieldPlace(f, off, 0, fs));
                off += fs;
                continue;
            }
            unitBytes = -1;
            off = RoundUp(off, fa);
            places.Add(new FieldPlace(f, off, 0, 0));
            off += fs;
        }
        return places;
    }

    private static int RoundUp(int v, int align) => align <= 1 ? v : (v + align - 1) / align * align;

    // ---- shared aggregate API for a second frontend (Zig) -----------------
    // The struct/enum field tables (StructFields / StructIsUnion / EnumTypes) are
    // private to the C build, but the registries they feed (Types / Enums) and the
    // layout/field-type model are frontend-neutral. These `internal` shims let the Zig
    // frontend register the SAME def records and resolve field types through the SAME
    // tables — no duplication, no behavior change for C, AOT-clean. The first shared-code
    // addition since the IFrontend seam (Zig Milestone D); see ZigLowering.

    /// <summary>Register a Zig struct/union under <paramref name="name"/>: add it to the
    /// emitted <see cref="Types"/> list AND the field/union layout tables so member access
    /// and <c>sizeof</c>/<c>offsetof</c> resolve through the same compile-time model the C
    /// frontend uses. Idempotent on the name — a second registration of the SAME shape is ignored,
    /// but one that would silently REDEFINE an existing aggregate (same name, different fields or
    /// union-ness) throws: the emitted C# has one type per name, so the second definition would be
    /// dropped and every use of it would read the first one's layout. An imported Zig module's containers
    /// are registered under module-qualified names (<c>fmt__Options</c>), so two modules no longer meet
    /// here; what remains is two C translation units defining a different <c>struct</c> of one tag, or a
    /// name clash no qualification covers — a loud error beats a silent miscompile.</summary>
    internal void RegisterStructType(string name, List<StructField> fields, bool isUnion, AggregateLayout layout = AggregateLayout.Default)
    {
        RejectReservedTypeName(name, isUnion ? "union" : "struct");
        if (StructFields.TryGetValue(name, out var already)
            && (isUnion != StructIsUnion[name] || !already.SequenceEqual(fields)))
        {
            throw new IrUnsupportedException(
                $"two different aggregates are both named '{name}' — the emitted C# can only carry one, so the "
                + "second definition would be silently dropped (C: two translation units defining a different "
                + $"`struct {name}`)");
        }
        if (EmittedTypes.Add(name))
        {
            StructFields[name] = fields;
            StructIsUnion[name] = isUnion;
            if (layout == AggregateLayout.Packed) { PackedStructs.Add(name); }
            Types.Add(new StructTypeDef(name, fields, isUnion, layout));
        }
    }

    /// <summary>The aggregates a lazily prepared Zig module registered (std, a sibling file), which the backend emits
    /// only when the program reaches them (task #194). zig analyses a declaration only when something references it, so
    /// std.Deque's test-only <c>FuzzAllocator</c> (a field of type <c>*std.testing.Smith</c>) must not bring std.Build,
    /// os.windows and dwarf into the program; a C or root-unit aggregate is always emitted.</summary>
    internal HashSet<string> PrunableTypes { get; } = new(System.StringComparer.Ordinal);

    /// <summary>Withdraw a registered struct / union so it is not emitted: a lazy Zig module's container
    /// found, after registering, to hold a container that could not be lowered (road-to-zig-std G3,
    /// <c>ZigLowering.FailDependentContainers</c>). A no-op for a name that is not registered.</summary>
    internal void WithdrawStructType(string name)
    {
        if (!EmittedTypes.Remove(name)) { return; }
        StructFields.Remove(name);
        StructIsUnion.Remove(name);
        PackedStructs.Remove(name);
        Types.RemoveAll(t => t.Name == name);
    }

    /// <summary>Replace a registered struct / union's field list (same names, rewritten types) in both the
    /// field table and the emitted type definition, keeping its position in <see cref="Types"/>.</summary>
    internal void ReplaceStructFields(string name, List<StructField> fields)
    {
        StructFields[name] = fields;
        var i = Types.FindIndex(t => t.Name == name);
        if (i >= 0) { Types[i] = Types[i] with { Fields = fields }; }
    }

    /// <summary>Register a Zig enum under <paramref name="name"/> with the given underlying
    /// integer type and members, mapping the name to its <see cref="CType.Enum"/> (so the
    /// name resolves as a real enum type) and emitting an <see cref="EnumTypeDef"/>. Returns
    /// the <see cref="CType.Enum"/>. Idempotent on the name — a second registration of the SAME shape
    /// (tag type and members, in order) is ignored — but one that would REDEFINE it throws, exactly as
    /// <see cref="RegisterStructType"/> does: the emitted C# carries one enum per name, so the second
    /// definition's members would be silently dropped while its uses resolved against the first (a
    /// type/codegen mismatch). An imported Zig module's enums are module-qualified, so this guards the
    /// C multi-TU case and any clash qualification does not cover.</summary>
    internal CType.Enum RegisterEnumType(string name, CType underlying, List<EnumMember> members)
    {
        if (!AddEnumDef(name, underlying, members)) { return EnumTypes[name]; }
        var enumType = new CType.Enum(name, underlying);
        EnumTypes[name] = enumType;
        return enumType;
    }

    /// <summary>Refuse a user type whose emitted name is one the runtime or the program shell already
    /// declares at the top level (<see cref="RuntimeTypeNames"/>) — it would transpile and then fail to
    /// build with C# CS0101, the "bad emit" the fail-loudly rule forbids. The Zig front-end never reaches
    /// this (it qualifies such a name); a C tag does, and renaming it is the author's call.</summary>
    internal static void RejectReservedTypeName(string name, string kind)
    {
        if (RuntimeTypeNames.IsReserved(name))
        {
            throw new IrUnsupportedException(
                $"the {kind} name '{name}' is reserved: dotcc's runtime declares a type of that name in the emitted "
                + "program, so the two would collide (rename the type)");
        }
    }

    /// <summary>Add an <see cref="EnumTypeDef"/> to the emitted enum set — the one place both front-ends
    /// go through. Returns false when an IDENTICAL definition (tag type and members, in order) is already
    /// registered, and throws when a DIFFERENT one is: the emitted C# has one enum per name, so the second
    /// would be dropped while its members' uses rendered against the first — a silent wrong value when a
    /// member name is shared. (C: two translation units each defining a different <c>enum color</c>,
    /// legal C that dotcc's single emitted program cannot represent; Zig: two modules' same-named enums.)</summary>
    internal bool AddEnumDef(string name, CType underlying, List<EnumMember> members)
    {
        RejectReservedTypeName(name, "enum");
        if (Enums.FirstOrDefault(e => e.Name == name) is { } existing)
        {
            if (!existing.Underlying.Equals(underlying) || !existing.Members.SequenceEqual(members))
            {
                throw new IrUnsupportedException(
                    $"two different enums are both named '{name}' — the emitted C# can only carry one, so the "
                    + "second definition would be silently dropped (C: two translation units defining a different "
                    + $"`enum {name}`)");
            }
            return false;
        }
        Enums.Add(new EnumTypeDef(name, underlying, members));
        return true;
    }

    /// <summary>The full declared field list of the registered struct/union
    /// <paramref name="name"/> (in declaration order), or <c>null</c> when no aggregate is
    /// registered under that name. Lets the Zig frontend enumerate a struct's fields to
    /// materialize defaults for any omitted from a <c>.{…}</c> literal.</summary>
    internal IReadOnlyList<StructField>? StructFieldsOf(string name) =>
        StructFields.TryGetValue(name, out var fields) ? fields : null;

    /// <summary>The declared type of <paramref name="field"/> on the struct/union that
    /// <paramref name="structType"/> names (pointer levels peeled, mirroring
    /// <see cref="MemberType"/>), or <c>null</c> when the type isn't a registered aggregate
    /// or has no such field — so the Zig frontend can raise a precise diagnostic rather than
    /// silently defaulting to <see cref="CType.Int"/> as the C member access does.</summary>
    internal CType? StructFieldType(CType structType, string field)
    {
        var t = structType.Unqualified;
        while (t is CType.Pointer p) { t = p.Pointee.Unqualified; }
        if (t is CType.Named n && StructFields.TryGetValue(n.Name, out var fields))
        {
            foreach (var f in fields) { if (f.Name == field) { return f.Type; } }
        }
        return null;
    }

    /// <summary>The canonical struct/union name an expression's type names (pointer
    /// levels peeled), or null if it isn't an aggregate.</summary>
    internal static string? StructCanonical(CType t)
    {
        while (t is CType.Pointer p) { t = p.Pointee; }
        return (t.Unqualified as CType.Named)?.Name;
    }
}
