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
        foreach (var f in fields)
        {
            var (fs, fa) = Layout(f.Type);
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
    /// <paramref name="structName"/> (0 for any union member), or null if unknown.</summary>
    internal int? OffsetOfConst(string structName, string member)
    {
        if (!StructFields.TryGetValue(structName, out var fields)) { return null; }
        if (StructIsUnion.GetValueOrDefault(structName)) { return 0; }
        var packed = PackedStructs.Contains(structName);   // byte-packed: no inter-field padding
        var off = 0;
        foreach (var f in fields)
        {
            var (fs, fa) = Layout(f.Type);
            if (!packed) { off = RoundUp(off, fa); }
            if (f.Name == member) { return off; }
            off += fs;
        }
        return null;
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
