#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>The type-CONSTRUCTING builtins and the compile-time diagnostics (road-to-zig-std S7) —
/// the direction opposite to <c>@typeInfo</c>. S5 reads a type's description out; <c>@Int</c> builds
/// a type back from one, and <c>@compileError</c> is how a comptime program rejects the type it was
/// handed.
///
/// <para><b><c>@Int(signedness, bits)</c></b> (207 uses) is the whole of the family that pays. The
/// other constructors are measured and cut: <c>@Pointer</c> ×14, <c>@Struct</c> ×5, <c>@Enum</c> ×4,
/// <c>@Union</c> ×3 — and each of those takes comptime AGGREGATE arguments (an attributes struct, a
/// <c>[]const []const u8</c> of field names, a <c>*const [N]type</c>), which is a comptime-value
/// engine dotcc does not have. They get loud cuts naming what they would need, not half-built
/// reifiers.</para>
///
/// <para><b>The exact-width rule carries over.</b> <c>@bitSizeOf</c> (431 uses, and the operand of
/// most <c>@Int</c> calls: <c>@Int(.unsigned, @bitSizeOf(T))</c>) is answered from the SAME declared
/// spelling <c>@typeInfo(T).int.bits</c> uses (<see cref="DeclaredBitsOfTypeArg"/>), because dotcc
/// widens an arbitrary-width <c>uN</c> to the smallest standard width and the lowered type alone
/// would answer 32 where zig says 21. Composing the two round-trips: <c>@Int(.unsigned,
/// @bitSizeOf(u21))</c> is <c>u21</c> again, and the width rides the binding it is given.</para>
///
/// <para><b><c>@compileError</c> fires where it is REACHED, not where it is written.</b> That is not
/// a dotcc leniency — it is what the language reference specifies: "this function, WHEN SEMANTICALLY
/// ANALYZED, causes a compile error… there are several ways that code avoids being semantically
/// checked, such as using <c>if</c> or <c>switch</c> with compile time constants". Lowering IS
/// dotcc's semantic analysis, and the comptime folds already installed (S5a selects one <c>switch</c>
/// prong, W3a folds a comptime <c>if</c> in a generic instance, S2 never lowers an unreferenced
/// module decl) are exactly the ways code avoids it. Measured against the pinned std, that is where
/// the uses live: of 595, <b>231 are an <c>else =&gt;</c> prong</b> of a folded <c>switch</c> — the
/// arm S5a never lowers — and the rest are comptime <c>if</c> guards inside <c>comptime T: type</c>
/// functions. So no poison value is needed for them; raising the diagnostic on sight is correct.
///
/// <para>ONE shape does need a poison, and it is the one the campaign will actually walk into: a
/// top-level <c>pub const NAME = @compileError("use X instead");</c> — a deprecation tombstone, 25
/// of them in the pin, in <c>std/meta.zig</c> and <c>std/os/windows.zig</c> among others. Zig
/// analyses a declaration only when something references it, so the tombstone is inert until named;
/// firing at the declaration would make importing those modules impossible. Hence
/// <see cref="_poisonedConsts"/>: the binding records the message and emits nothing, and the
/// diagnostic is raised at the REFERENCE — in value position and in type position alike.</para></para>
///
/// <para>Nothing here reaches the IR: a constructed type is a <see cref="CType"/>, a width is a
/// literal, and a diagnostic throws. The value/type firewall in the comptime interpreter is
/// untouched, exactly as in S5/S6.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>Each name bound to a <c>@compileError</c>, mapped to the author's message — a
    /// DEPRECATION TOMBSTONE (<c>pub const MACH_PORT_RIGHT = @compileError("use MACH.PORT.RIGHT");</c>).
    /// The declaration emits nothing and raises nothing; naming it raises. Name-keyed and
    /// function-flat like <see cref="_comptimeValues"/> / <see cref="_typeAliases"/> — the same W1
    /// leniency, and harmless here for the same reason: a poisoned name has no runtime symbol, so
    /// every lookup is guarded on the ordinary resolution having already missed.</summary>
    private readonly Dictionary<string, string> _poisonedConsts = new(System.StringComparer.Ordinal);

    /// <summary>The comptime eval-step budget in force, raised by <c>@setEvalBranchQuota</c> and never
    /// lowered (see <see cref="SetEvalBranchQuota"/>). Starts at the
    /// <see cref="IrModule.DefaultComptimeStepBudget"/> dotcc has always used.</summary>
    private long _evalStepBudget = IrModule.DefaultComptimeStepBudget;

    /// <summary>The width each lowered <c>@Int(…)</c> SITE was constructed with, keyed by AST
    /// reference like <see cref="_inlineStructNames"/>. dotcc widens <c>u21</c> to a 32-bit
    /// <c>uint</c>, so the constructed <see cref="CType"/> no longer knows it was built with 21 —
    /// this is how the width reaches <see cref="DeclaredBitsOfTypeArg"/> and therefore rides the
    /// binding, exactly as a source SPELLING does (S5b). Recording it at the site also means the
    /// width operand is evaluated once, not again per query.</summary>
    private readonly Dictionary<Item, int> _reifiedIntBits = new(ReferenceEqualityComparer.Instance);

    // ---- @Int and the rest of the reification family ----------------------

    /// <summary>Recognize and lower a builtin that CONSTRUCTS a type, for the type positions
    /// (<see cref="LowerType"/> and the type-alias RHS). <c>@Int</c> builds; every other member of the
    /// family is a loud cut naming what it would take. Returns false for a builtin that is not one of
    /// them (<c>@TypeOf</c>, <c>@This</c> — handled by their own cases), so the caller falls through
    /// to its own dispatch.</summary>
    private bool TryLowerReifyBuiltin(Zig.BuiltinCall b, out CType type)
    {
        type = CType.Void;
        var name = Tok(b.Arg0);
        switch (name)
        {
            case "@Int":
                type = IntBuiltinType(b);
                return true;

            // The AGGREGATE constructors. Every one takes comptime aggregate arguments — a
            // `[]const []const u8` of field names, a `*const [N]type`, an attributes struct — which is
            // the comptime-value engine dotcc has not built (S5 folds types, not aggregate VALUES).
            // Reifying from a partially-understood description would emit a wrong layout, so these are
            // named cuts. Their measured use count is the reason the order of work is what it is.
            // `@Struct` IS modeled as the result of a type-returning function (ReifyStructBuiltin, task #93), which
            // names the struct after its instance; anywhere else it has no name to take.
            case "@Struct":
                throw new IrUnsupportedException(
                    "zig `@Struct(…)` is modeled as a type-returning function's result (`fn S(…) type { return "
                    + "@Struct(…); }`), which names the struct after the instance; here it has no name to take");
            // `@Enum` is modeled as a type-returning function's result, as `@Struct` is (ReifyEnumBuiltin, task #108).
            case "@Enum":
                throw new IrUnsupportedException(
                    "zig `@Enum(…)` is modeled as a type-returning function's result (`fn E(…) type { return "
                    + "@Enum(…); }`), which names the enum after the instance; here it has no name to take");
            case "@Union" or "@Pointer" or "@Fn" or "@Tuple":
                throw new IrUnsupportedException(
                    $"zig `{name}(…)` reifies a type from comptime AGGREGATE arguments (field-name and "
                    + "field-type arrays, an attributes struct), which dotcc has no comptime-aggregate engine "
                    + "for — `@Int` is the member of the family that is modeled (road-to-zig-std S7). Spell the "
                    + "type, or build it with `@Int` when it is an integer");

            // SIMD: .NET's own vector types (the target-identity segment T5, ZigLowering.Vector.cs).
            case "@Vector":
                type = VectorTypeOf(b);
                return true;

            // `@FieldType(Elem, @tagName(field))` (std.MultiArrayList's `items` return type, task #108).
            case "@FieldType":
                type = FieldTypeBuiltin(b);
                return true;

            default:
                return false;
        }
    }

    /// <summary><c>@FieldType(T, name)</c>: the type of the field <c>name</c> of the struct or union <c>T</c>, the name
    /// comptime-known (a string, or <c>@tagName</c> of a comptime enum value, as std.MultiArrayList's
    /// <c>FieldType(comptime field: Field)</c> spells it, task #108). A union's field is its variant's payload type.</summary>
    private CType FieldTypeBuiltin(Zig.BuiltinCall call)
    {
        var args = Flatten(call.Arg2);
        if (args.Count != 2)
        {
            throw new IrUnsupportedException($"zig `@FieldType` expects (type, name); got {args.Count} argument(s)");
        }
        var container = LowerType(args[0]);
        if (ContainerTypeName(container) is not { } containerName)
        {
            throw new IrUnsupportedException($"zig `@FieldType`: {container.Describe()} is not a struct or union type");
        }
        var name = ComptimeName(args[1]) ?? TryComptimeTagName(args[1])
            ?? throw new IrUnsupportedException("zig `@FieldType`: the field name must be comptime-known");
        foreach (var agg in AggregatesOf(containerName))
        {
            if (_ir.StructFields.TryGetValue(agg, out var fields) && fields.FirstOrDefault(f => f.Name == name) is { Type: { } fieldType })
            {
                return fieldType;
            }
        }
        throw new IrUnsupportedException($"zig `@FieldType`: '{containerName}' has no field '{name}'");
    }

    // ---- @Struct: a struct from comptime field lists (task #93) ------------

    /// <summary>Evaluate a type body's <c>return @Struct(layout, BackingInt, field_names, field_types, field_attrs)</c>
    /// (std.enums.EnumFieldStruct) to the fields of the struct it builds. Every list is evaluated now: the names
    /// (a spelled list, a <c>@typeInfo</c> member list or a call returning one, <c>std.meta.fieldNames(E)</c>), the
    /// types (<c>&amp;@splat(T)</c> or a spelled list) and the attributes (<c>&amp;@splat(.{ .default_value_ptr = p })</c>),
    /// whose default pointer is one <see cref="TryBindTypeBodyDefaultPtr"/> bound. The caller registers the struct under
    /// the instance's name, so each instance is one type, as for <c>return struct {…}</c>.</summary>
    private TypeBodyResult ReifyStructBuiltin(string fnName, Zig.BuiltinCall call)
    {
        var args = Flatten(call.Arg2);
        if (args.Count != 5)
        {
            throw new IrUnsupportedException(
                $"zig `@Struct` expects (layout, BackingInt, field_names, field_types, field_attrs); got {args.Count} argument(s)");
        }
        var layout = ReifiedStructLayout(args[0]);
        if (!IsComptimeNull(args[1]))
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': `@Struct` with a backing integer (a packed struct) is not modeled");
        }
        var names = ReifiedFieldNames(fnName, args[2]);
        var types = ReifiedList(fnName, args[3], names.Count, "field_types", LowerType);
        var defaults = ReifiedList(fnName, args[4], names.Count, "field_attrs", attr => ReifiedFieldDefault(fnName, attr));
        var fields = new List<ReifiedField>(names.Count);
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            if (!seen.Add(names[i]))
            {
                throw new IrUnsupportedException($"zig `@Struct`: duplicate struct field name '{names[i]}'");
            }
            fields.Add(new ReifiedField(names[i], types[i], defaults[i]));
        }
        return new TypeBodyResult(true, null, null, null, layout, fields);
    }

    /// <summary>Evaluate a type-returning body's <c>return @Enum(TagInt, mode, field_names, field_values);</c> (std.meta.FieldEnum,
    /// task #108): the tag integer type, whether the enum is non-exhaustive, the member names (as <c>@Struct</c>'s are
    /// read) and their values (a spelled <c>&amp;.{ … }</c>, or anything the interpreter evaluates to an integer array, as
    /// <c>&amp;std.simd.iota(IntTag, n)</c>). Registered under the instance's name by the caller.</summary>
    private TypeBodyResult ReifyEnumBuiltin(string fnName, Zig.BuiltinCall call)
    {
        var args = Flatten(call.Arg2);
        if (args.Count != 4)
        {
            throw new IrUnsupportedException(
                $"zig `@Enum` expects (TagInt, mode, field_names, field_values); got {args.Count} argument(s)");
        }
        var tag = LowerType(args[0]).Unqualified;
        if (tag is not CType.Prim { Integer: true })
        {
            throw new CompileException($"zig: `@Enum`'s tag type must be an integer type, not {tag.Describe()}");
        }
        var nonExhaustive = ReifiedEnumMode(args[1]);
        var names = ReifiedFieldNames(fnName, args[2]);
        var values = ReifiedEnumValues(fnName, args[3], names.Count, tag);
        var seenNames = new HashSet<string>(System.StringComparer.Ordinal);
        var seenValues = new HashSet<long>();
        for (var i = 0; i < names.Count; i++)
        {
            if (!seenNames.Add(names[i])) { throw new CompileException($"zig: `@Enum`: duplicate enum field name '{names[i]}'"); }
            if (!seenValues.Add(values[i])) { throw new CompileException($"zig: `@Enum`: enum tag value {values[i]} already taken"); }
        }
        return new TypeBodyResult(false, null, null, null,
            Enum: new ReifiedEnum(tag, DeclaredBitsOfTypeArg(args[0]), names, values, nonExhaustive));
    }

    /// <summary><c>@Enum</c>'s mode: <c>.exhaustive</c> (false) or <c>.nonexhaustive</c> (true).</summary>
    private static bool ReifiedEnumMode(Item arg)
    {
        var cur = arg;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        return cur.Content is Zig.EnumLit lit
            ? Tok(lit.Arg1) switch
            {
                "exhaustive" => false,
                "nonexhaustive" => true,
                var other => throw new CompileException($"zig: `@Enum`: `.{other}` is not an enum mode"),
            }
            : throw new IrUnsupportedException("zig `@Enum`: the mode must be a comptime-known `.exhaustive` or `.nonexhaustive`");
    }

    /// <summary><c>@Enum</c>'s member values (<c>*const [n]TagInt</c>): a spelled list, each element a constant, or an
    /// integer array the interpreter evaluates (<c>&amp;std.simd.iota(IntTag, n)</c>). The wrong count is zig's type error.</summary>
    private List<long> ReifiedEnumValues(string fnName, Item arg, int count, CType tag)
    {
        var cur = StripAddrOf(arg);
        List<long>? values = null;
        if (cur.Content is Zig.AnonStructInitEmpty) { values = new List<long>(); }
        else if (cur.Content is Zig.AnonStructInit anon
                 && Flatten(anon.Arg2) is var inits
                 && inits.Select(i => i.Content).OfType<Zig.FieldInitPositional>().ToList() is var positional
                 && positional.Count == inits.Count)
        {
            values = positional.Select(p => _ir.ConstEval(LowerExprSink(p.Arg0, tag))
                ?? throw new IrUnsupportedException($"type-returning generic '{fnName}': an `@Enum` value must be a constant")).ToList();
        }
        else
        {
            IrModule.ComptimeValue? value;
            using (EnterThrowawayHoist()) { value = _ir.EvalComptimeValue(LowerExprSink(arg, new CType.Pointer(new CType.Array(tag, count)))); }
            IEnumerable<IrModule.ComptimeValue>? elems = value switch
            {
                IrModule.CtArray arr => arr.Elems,
                IrModule.CtElemPtr { Index: 0 } ep => ep.Backing.Elems,
                IrModule.CtSlice sl => sl.Backing.Elems.Skip((int)sl.Offset).Take((int)sl.Length),
                _ => null,
            };
            if (elems?.Select(e => e is IrModule.CtInt ci ? (long?)(long)ci.Value : null).ToList() is { } evaluated
                && evaluated.All(v => v is not null))
            {
                values = evaluated.Select(v => v ?? 0).ToList();
            }
        }
        if (values is null)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': `@Enum`'s field values must be a spelled list or a comptime-known integer array"
                + (_ir.ComptimeMiss is { } why ? $" (the interpreter stopped at {why})" : ""));
        }
        if (values.Count != count)
        {
            throw new CompileException($"zig: `@Enum`: {values.Count} field value(s) for {count} field name(s)");
        }
        return values;
    }

    /// <summary>Register an enum <c>@Enum</c> built (<see cref="ReifyEnumBuiltin"/>) under <paramref name="name"/>: its members
    /// and their symbols, as a declared <c>enum(TagInt) { … }</c> registers them, with the tag type spelled.</summary>
    private CType.Enum RegisterReifiedEnum(string name, ReifiedEnum reified)
    {
        var enumType = new CType.Enum(name, reified.Tag);
        var members = new List<EnumMember>(reified.Names.Count);
        var memberSyms = new Dictionary<string, Symbol>(System.StringComparer.Ordinal);
        for (var i = 0; i < reified.Names.Count; i++)
        {
            members.Add(new EnumMember(reified.Names[i], reified.Values[i]));
            memberSyms[reified.Names[i]] = new Symbol
            {
                Name = reified.Names[i], Kind = SymKind.EnumConst, Type = enumType, ConstValue = reified.Values[i], IsGlobal = true,
            };
        }
        _ir.RegisterEnumType(name, reified.Tag, members);
        _containerTypes[name] = enumType;
        _enumMembers[name] = memberSyms;
        _enumsWithSpelledTag.Add(name);
        if (reified.NonExhaustive) { _nonExhaustiveEnums.Add(name); }
        return enumType;
    }

    /// <summary>Register a struct <c>@Struct</c> built (<see cref="ReifyStructBuiltin"/>): its fields, and each default in
    /// <see cref="_reifiedFieldDefaults"/>, where a struct literal omitting that field reads it.</summary>
    private void RegisterReifiedStruct(string name, IReadOnlyList<ReifiedField> fields, AggregateLayout layout)
    {
        foreach (var f in fields)
        {
            if (f.Default is { } d) { _reifiedFieldDefaults[(name, f.Name)] = d; }
        }
        _ir.RegisterStructType(name, fields.Select(f => new StructField(f.Name, f.Type)).ToList(), isUnion: false, layout);
    }

    /// <summary>Each <c>@Struct</c>-built field's default, already a literal (the other field defaults are ASTs, see
    /// <see cref="_structFieldDefaults"/>, lowered where they are used).</summary>
    private readonly Dictionary<(string Struct, string Field), CExpr> _reifiedFieldDefaults = new();

    /// <summary><c>@Struct</c>'s layout argument: <c>.auto</c> or <c>.@"extern"</c>. A packed one needs its backing
    /// integer, which is not modeled.</summary>
    private static AggregateLayout ReifiedStructLayout(Item arg)
    {
        var cur = arg;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        if (cur.Content is not Zig.EnumLit lit)
        {
            throw new IrUnsupportedException("zig `@Struct`: the layout must be a comptime-known `.auto` or `.@\"extern\"`");
        }
        return Tok(lit.Arg1) switch
        {
            "auto" => AggregateLayout.Default,
            "extern" => AggregateLayout.Sequential,
            "packed" => throw new IrUnsupportedException("zig `@Struct(.@\"packed\", …)`: a packed reified struct is not modeled"),
            var other => throw new IrUnsupportedException($"zig `@Struct`: `.{other}` is not a container layout"),
        };
    }

    /// <summary>The field NAMES of a <c>@Struct</c>: a spelled list of string literals (<c>&amp;.{ "a", "b" }</c>), a
    /// <c>@typeInfo</c> member list, or anything the interpreter evaluates to a slice of strings (a call returning one,
    /// <c>std.meta.fieldNames(E)</c>).</summary>
    private IReadOnlyList<string> ReifiedFieldNames(string fnName, Item arg)
    {
        if (TryFoldTypeInfoList(arg, out var list) && list.Strings is { } folded) { return folded; }
        var cur = StripAddrOf(arg);
        if (cur.Content is Zig.AnonStructInitEmpty) { return System.Array.Empty<string>(); }
        if (cur.Content is Zig.AnonStructInit anon)
        {
            List<string>? spelled = new();
            foreach (var init in Flatten(anon.Arg2))
            {
                if (init.Content is not Zig.FieldInitPositional { Arg0.Content: Zig.StrLit str }) { spelled = null; break; }
                spelled.Add(UnquoteStringLiteral(Tok(str.Arg0)));
            }
            if (spelled is not null) { return spelled; }
        }
        var namesType = new CType.Slice(new CType.Slice(CType.UChar.WithQuals(TypeQual.Const)).WithQuals(TypeQual.Const));
        IrModule.ComptimeValue? value;
        using (EnterThrowawayHoist()) { value = _ir.EvalComptimeValue(LowerExprSink(arg, namesType)); }
        IEnumerable<IrModule.ComptimeValue>? elems = value switch
        {
            IrModule.CtSlice sl => sl.Backing.Elems.Skip((int)sl.Offset).Take((int)sl.Length),
            IrModule.CtArray arr => arr.Elems,
            _ => null,
        };
        var names = new List<string>();
        foreach (var e in elems ?? [])
        {
            if (ComptimeString(e) is not { } n) { elems = null; break; }
            names.Add(n);
        }
        if (elems is null)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': `@Struct`'s field names must be comptime-known strings");
        }
        return names;
    }

    /// <summary>A comptime string (a slice or array of bytes) as text; null when the value is not one.</summary>
    private static string? ComptimeString(IrModule.ComptimeValue value)
    {
        IEnumerable<IrModule.ComptimeValue>? bytes = value switch
        {
            IrModule.CtSlice sl => sl.Backing.Elems.Skip((int)sl.Offset).Take((int)sl.Length),
            IrModule.CtArray arr => arr.Elems,
            _ => null,
        };
        if (bytes is null) { return null; }
        var sb = new System.Text.StringBuilder();
        foreach (var b in bytes)
        {
            if (b is not IrModule.CtInt ci) { return null; }
            sb.Append((char)(byte)ci.Value);
        }
        return sb.ToString();
    }

    /// <summary>A per-field list argument of <c>@Struct</c> (<c>*const [n]T</c>): <c>&amp;@splat(x)</c> repeats one
    /// element, a spelled <c>&amp;.{ a, b }</c> gives one each, and a <c>@typeInfo</c> <c>field_types</c> list gives the
    /// types. Each element is evaluated by <paramref name="each"/>; a list of the wrong length is zig's type error.</summary>
    private List<T> ReifiedList<T>(string fnName, Item arg, int count, string what, System.Func<Item, T> each)
    {
        var cur = StripAddrOf(arg);
        if (cur.Content is Zig.BuiltinCall { Arg0: var splatTok } splat && Tok(splatTok) == "@splat" && Flatten(splat.Arg2) is [var one])
        {
            var element = each(one);
            return Enumerable.Repeat(element, count).ToList();
        }
        List<T>? items = null;
        if (cur.Content is Zig.AnonStructInitEmpty) { items = new List<T>(); }
        else if (cur.Content is Zig.AnonStructInit anon
                 && Flatten(anon.Arg2) is var inits
                 && inits.Select(i => i.Content).OfType<Zig.FieldInitPositional>().ToList() is var positional
                 && positional.Count == inits.Count)
        {
            items = positional.Select(p => each(p.Arg0)).ToList();
        }
        else if (typeof(T) == typeof(CType) && TryFoldTypeInfoList(arg, out var list) && list.Types is { } folded)
        {
            items = folded.Cast<T>().ToList();
        }
        if (items is null)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': `@Struct`'s {what} must be `&@splat(x)` or a spelled list `&.{{ … }}`");
        }
        if (items.Count != count)
        {
            throw new IrUnsupportedException(
                $"zig `@Struct`: {what} has {items.Count} element(s) but there are {count} field name(s)");
        }
        return items;
    }

    /// <summary>One field's attributes (<c>.{ .default_value_ptr = p, .@"comptime" = false, .@"align" = null }</c>) to its
    /// default value: null for no default. A comptime field is not modeled; an alignment is the same leniency as
    /// <c>x: T align(N)</c> (C#'s layout places the field).</summary>
    private CExpr? ReifiedFieldDefault(string fnName, Item attr)
    {
        var cur = attr;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        if (cur.Content is Zig.AnonStructInitEmpty) { return null; }
        if (cur.Content is not Zig.AnonStructInit anon)
        {
            throw new IrUnsupportedException(
                $"type-returning generic '{fnName}': a `@Struct` field attribute must be an anonymous literal `.{{ … }}`");
        }
        CExpr? def = null;
        foreach (var init in Flatten(anon.Arg2))
        {
            if (init.Content is not Zig.FieldInit fi)
            {
                throw new IrUnsupportedException("zig `@Struct` field attributes are named: `.{ .default_value_ptr = … }`");
            }
            switch (Tok(fi.Arg1))
            {
                case "default_value_ptr":
                    if (IsComptimeNull(fi.Arg3)) { def = null; break; }
                    if (fi.Arg3.Content is Zig.Ident pid && _typeBodyDefaultPtrs.TryGetValue(Tok(pid.Arg0), out var pointee))
                    {
                        def = pointee;
                        break;
                    }
                    throw new IrUnsupportedException(
                        $"type-returning generic '{fnName}': `.default_value_ptr` must be null or a pointer to a comptime "
                        + "default (`const p: ?*const anyopaque = if (default) |d| @ptrCast(&d) else null;`)");
                case "comptime":
                    if (FoldTypeBodyCondition(fnName, fi.Arg3))
                    {
                        throw new IrUnsupportedException("zig `@Struct`: a `comptime` field is not modeled");
                    }
                    break;
                case "align":
                    break;
                case var other:
                    throw new IrUnsupportedException($"zig `@Struct`: `{other}` is not a field attribute");
            }
        }
        return def;
    }

    /// <summary>Bind a type-body const that is a comptime POINTER to a default value,
    /// <c>if (field_default) |d| @ptrCast(&amp;d) else null</c> (with or without the <c>@ptrCast</c>), into
    /// <see cref="_typeBodyDefaultPtrs"/>: the payload as a literal, or null when the optional is null. False when the
    /// initializer is not that shape.</summary>
    private bool TryBindTypeBodyDefaultPtr(string name, Item rhs)
    {
        var cur = rhs;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        if (cur.Content is not Zig.IfExprCapture ic || !IsComptimeNull(ic.Arg9)) { return false; }
        var target = ic.Arg7;
        while (target.Content is Zig.Grouped tg) { target = tg.Arg1; }
        if (target.Content is Zig.BuiltinCall { Arg0: var castTok } cast && Tok(castTok) == "@ptrCast" && Flatten(cast.Arg2) is [var castArg])
        {
            target = castArg;
        }
        if (target.Content is not Zig.PreAddrOf { Arg1.Content: Zig.Ident pointee } || Tok(pointee.Arg0) != Tok(ic.Arg5))
        {
            return false;
        }
        if (!TryComptimeOptionalCond(ic.Arg2, out var opt))
        {
            throw new IrUnsupportedException(
                $"zig `const {name}`: a pointer to a default value needs a comptime-known optional in a type body");
        }
        _typeBodyDefaultPtrs[name] = opt.HasValue ? ComptimeScalarLiteral(opt.Value, opt.Inner) : null;
        return true;
    }

    /// <summary>A comptime scalar as a literal of <paramref name="type"/>: a <c>bool</c> as <c>true</c> / <c>false</c>, an
    /// enum as its tag cast to the enum, an integer as itself.</summary>
    private static CExpr ComptimeScalarLiteral(long value, CType type)
    {
        if (type.Unqualified == CType.Bool) { return new LitBool(value != 0) { Type = CType.Bool }; }
        return type.Unqualified is CType.Enum
            ? new Cast(type, ComptimeVarLit(value, CType.Long)) { Type = type }
            : ComptimeVarLit(value, type);
    }

    /// <summary>The operand of <c>&amp;x</c> (or <c>x</c> itself), through parentheses.</summary>
    private static Item StripAddrOf(Item arg)
    {
        var cur = arg;
        while (cur.Content is Zig.Grouped g) { cur = g.Arg1; }
        if (cur.Content is Zig.PreAddrOf a) { cur = a.Arg1; }
        while (cur.Content is Zig.Grouped g2) { cur = g2.Arg1; }
        return cur;
    }

    /// <summary>Lower <c>@Int(signedness, bits)</c> to the integer <see cref="CType"/> it names —
    /// <c>@Int(.unsigned, 18)</c> is <c>u18</c>, so it goes through the SAME
    /// <see cref="TryArbitraryWidthInt"/> widening a spelled <c>u18</c> does (there is one widening
    /// rule in the front end, and this is it). The declared width is not lost: a binding records it
    /// via <see cref="DeclaredBitsOfTypeArg"/>, so <c>@typeInfo(@Int(.unsigned, 21)).int.bits</c>
    /// answers 21.</summary>
    /// <summary>Above zero while a type-returning call's TYPE argument lowers (a pure type computation, where a
    /// wider-than-128 integer may appear, see <see cref="IntBuiltinType"/>).</summary>
    private int _typeArgDepth;

    private CType IntBuiltinType(Zig.BuiltinCall call)
    {
        var args = Flatten(call.Arg2);
        if (args.Count != 2)
        {
            throw new IrUnsupportedException(
                $"zig `@Int` expects (signedness, bits); got {args.Count} argument(s)");
        }
        var signed = ComptimeSignedness(args[0]);
        var bits = ComptimeBitCount(args[1], "@Int");
        _reifiedIntBits[call.Arg2] = bits;   // the width the site was built with — see _reifiedIntBits
        var spelling = (signed ? "i" : "u") + bits.ToString(CultureInfo.InvariantCulture);
        if (!TryArbitraryWidthInt(spelling, out var type))
        {
            // A WIDE integer built inside a type-returning body, or as a type-returning call's TYPE argument
            // (`std.math.Log2Int(@Int(.unsigned, 384))` in std.Target's Feature.Set) is only a width for another type computation
            // to read: it is carried as 128 bits with its declared width recorded above, which is what
            // `@typeInfo(T).int.bits` answers. A runtime value of one, outside such a body, stays loud.
            if (bits > 128 && (_typeBodiesInProgress.Count > 0 || _typeArgDepth > 0)) { return signed ? CType.Int128 : CType.UInt128; }
            throw new IrUnsupportedException(
                $"zig `@Int(.{(signed ? "signed" : "unsigned")}, {bits})`: dotcc models integer widths 1..128 "
                + $"(`{spelling}` is outside that range; a wider one needs BigInteger)");
        }
        return type;
    }

    /// <summary>Evaluate <c>@Int</c>'s first argument to a signedness. Two producers, matching how the
    /// pinned std writes it: the enum literal <c>.unsigned</c> / <c>.signed</c> (190 of the 207 uses)
    /// and a folded <c>@typeInfo(T).int.signedness</c> (the remaining 17, via
    /// <see cref="TryEvalComptimeTag"/> — which is exactly recoverable, since dotcc's width widening
    /// never changes a type's signedness).</summary>
    private bool ComptimeSignedness(Item item)
    {
        if (item.Content is Zig.Grouped g) { return ComptimeSignedness(g.Arg1); }
        var tag = item.Content is Zig.EnumLit lit
            ? Tok(lit.Arg1)
            : TryEvalComptimeTag(item, out var evaluated, out _) ? evaluated : null;
        return tag switch
        {
            "unsigned" => false,
            "signed" => true,
            null => throw new IrUnsupportedException(
                "zig `@Int`: the signedness must be comptime-known — `.unsigned` / `.signed`, or a "
                + "`@typeInfo(T).int.signedness` that folds"),
            _ => throw new IrUnsupportedException(
                $"zig `@Int`: `.{tag}` is not a signedness — expected `.unsigned` or `.signed`"),
        };
    }

    /// <summary>Evaluate a comptime BIT COUNT — <c>@Int</c>'s width argument. Any expression the
    /// const-folder can settle works, which is what the measured shapes need:
    /// <c>@bitSizeOf(T)</c> (the commonest by far), <c>@typeInfo(T).int.bits</c>, a comptime
    /// parameter, a literal, and arithmetic over those (<c>bits * 2</c>, <c>info.bits + 1</c>).
    /// Lowered through <see cref="LowerExpr"/> exactly as <see cref="ConstEvalArraySize"/> does, so
    /// every fold already installed is available here for free.</summary>
    private int ComptimeBitCount(Item item, string what)
    {
        var lowered = InlineUnfoldedConsts(LowerExpr(item));
        // A width that CALLS (std.math.IntFittingRange's `1 + log2(pos_max)`) runs through the interpreter.
        var n = _ir.ConstEval(lowered)
            ?? (_ir.EvalComptimeValue(lowered) is IrModule.CtInt { Value: var big } && big >= long.MinValue && big <= long.MaxValue
                ? (long)big : null);
        if (n is null)
        {
            throw new IrUnsupportedException(
                $"zig `{what}`: the bit width must be a comptime-known integer (a literal, `@bitSizeOf(T)`, "
                + "`@typeInfo(T).int.bits`, a `comptime` parameter, or arithmetic over those)");
        }
        if (n < 0 || n > int.MaxValue)
        {
            throw new IrUnsupportedException($"zig `{what}`: the bit width {n} is out of range");
        }
        return (int)n.Value;
    }

    // ---- @bitSizeOf -------------------------------------------------------

    /// <summary>Lower <c>@bitSizeOf(T)</c> — zig's "bits this type occupies as a field of a packed
    /// struct" — to a comptime literal. Answered from the type's DECLARED SPELLING first
    /// (<see cref="DeclaredBitsOfTypeArg"/>, the S5b machinery that also travels with a
    /// <c>comptime T: type</c> binding), because dotcc widens <c>u21</c> to a 32-bit <c>uint</c> and
    /// the lowered type would answer 32 where zig says 21. Only when no spelling is available does it
    /// fall back to the lowered type, and only for the kinds whose width is EXACTLY recoverable —
    /// which the measured operands are almost entirely made of (<c>usize</c> ×78, a
    /// <c>comptime T: type</c> ×69, integer aliases, and enums).
    ///
    /// <para>Typed <c>int</c> rather than the <c>comptime_int</c> zig gives it, for the same reason
    /// <c>@typeInfo(T).array.len</c> is: the literal then renders bare and C#'s implicit CONSTANT
    /// conversion lets it land in any integer sink, where a suffixed one would not (CS0266).</para></summary>
    private CExpr BitSizeOfBuiltin(IReadOnlyList<Item> args)
    {
        if (args.Count != 1)
        {
            throw new IrUnsupportedException($"zig `@bitSizeOf` expects (type); got {args.Count} argument(s)");
        }
        var bits = ZigBitWidth(args[0]);
        return new LitInt(bits.ToString(CultureInfo.InvariantCulture), bits) { Type = CType.Int };
    }

    /// <summary>The zig bit width of a type ARGUMENT: its declared spelling when there is one, else
    /// the exactly-recoverable width of the lowered type. Throws the fidelity cut when neither
    /// answers — the same judgement <c>@typeInfo(T).int.bits</c> makes, and for the same reason: a
    /// silently-32 answer where zig says 21 is the one outcome worth refusing.</summary>
    private int ZigBitWidth(Item typeAst)
    {
        // Lower FIRST: a constructed type's width (an `@Int` site, a type-returning call) is recorded by
        // the lowering itself, so asking before it would miss it and fall through to the widened width.
        var type = LowerType(typeAst);
        if (DeclaredBitsOfTypeArg(typeAst) is { } declared) { return declared; }
        if (ExactBitWidth(type) is { } exact) { return exact; }
        throw new IrUnsupportedException(
            $"zig `@bitSizeOf({type.Describe()})`: only a scalar (integer, float, bool, void, pointer) or an "
            + "enum has a bit width dotcc can state exactly. An aggregate's is its byte size in bits, and dotcc "
            + "byte-packs its own layout, so it may disagree with zig — use `@sizeOf(T) * 8` to opt into that "
            + "approximation deliberately (road-to-zig-std S7)");
    }

    /// <summary>The bit width of a lowered type when it is EXACTLY recoverable, else null. A pointer
    /// is 8 bytes on this LP64 target; an enum is its tag type; <c>bool</c> is one bit (zig's
    /// packed-field width, not the byte dotcc stores it in) and <c>void</c> is zero. An aggregate
    /// returns null deliberately — see the cut in <see cref="ZigBitWidth"/>.</summary>
    private int? ExactBitWidth(CType type)
    {
        var t = type.Unqualified;
        if (t.Equals(CType.Bool)) { return 1; }
        return t switch
        {
            CType.VoidType => 0,
            // `void` as data (task #114's runtime `Unit`, std.array_hash_map's `Hash = void`) has no bits, as `void` has.
            CType.Named { Name: "Unit" } => 0,
            CType.Pointer => 64,
            CType.Enum e => ExactBitWidth(e.Underlying),
            CType.Prim p => p.Bytes * 8,
            CType.Named n => UniformStructBits(n.Name),
            _ => null,
        };
    }

    /// <summary>The <c>@bitSizeOf</c> of a plain (not packed, not union) struct whose fields are all scalars of ONE byte
    /// size, which is also their alignment (std.sort's comptime <c>Data { size: usize, size_index: usize, alignment: usize
    /// }</c> through std.mem.reverse's <c>@bitSizeOf(T) &gt; 0</c>, task #108). zig defines a non-packed struct's bit size as
    /// <c>@sizeOf(T) * 8</c>, and such fields leave neither zig's layout nor dotcc's any padding to add or reorder, so both
    /// agree on the size. Null for any other aggregate, which stays the loud S7 cut.</summary>
    private int? UniformStructBits(string name)
    {
        if (_ir.StructIsUnion.GetValueOrDefault(name) || _ir.PackedStructs.Contains(name)
            || _ir.StructFieldsOf(name) is not { Count: > 0 } fields)
        {
            return null;
        }
        int? width = null;
        foreach (var f in fields)
        {
            if (f.Type.Unqualified is not (CType.Prim { Integer: true } or CType.Prim { Integer: false } or CType.Pointer or CType.Enum)
                || f.Type.Equals(CType.Bool) || ExactBitWidth(f.Type) is not { } bits || bits % 8 != 0
                || (width is { } w && w != bits))
            {
                return null;
            }
            width = bits;
        }
        return width is { } uniform ? uniform * fields.Count : null;
    }

    // ---- @compileError / @compileLog / @setEvalBranchQuota ----------------

    /// <summary>Raise a <c>@compileError(msg)</c>. Reaching this point IS the semantic analysis zig
    /// specifies the diagnostic on: every way a real compiler avoids analysing the call — a folded
    /// <c>switch</c> prong, a comptime <c>if</c>, an unreferenced declaration — is a fold that already
    /// ran, so a call that arrives here is one zig would have raised too. The author's own message is
    /// carried through verbatim when it is comptime-readable, because that message is the entire point
    /// of the builtin ("this type does not support …", naming the type via <c>++ @typeName(T)</c>).</summary>
    private CExpr CompileErrorBuiltin(IReadOnlyList<Item> args)
    {
        if (args.Count != 1)
        {
            throw new IrUnsupportedException($"zig `@compileError` expects (message); got {args.Count} argument(s)");
        }
        if (ComptimeMessageText(args[0]) is { } message)
        {
            throw new IrUnsupportedException("zig `@compileError`: " + message);
        }
        // A message built from a RUNTIME value (`parser.specifier() catch |err| @compileError(@errorName(err))`
        // in std.fmt.Placeholder.parse): zig accepts that only on a path it evaluates at comptime, and raises
        // it only if the evaluation takes it. dotcc lowers such a body as runtime code too, so the arm
        // becomes the `unreachable` trap, and the comptime interpreter stops if it ever reaches it.
        return new Call("__dotcc_unreachable", new List<CExpr>(), new List<CType>(), null) { Type = CType.Void };
    }

    /// <summary>What a <c>@compileError</c> / <c>@compileLog</c> message reads as when it is not
    /// comptime-readable in the forms modeled — better than dropping the diagnostic, and it still
    /// tells the reader the program deliberately rejected this instantiation.</summary>
    private const string UnreadableMessage = "(the message is not a comptime-readable string here)";

    /// <summary>Read a comptime message string as PLAIN text. The literal and captured-name cases are
    /// <see cref="ComptimeStringArg"/>'s; this adds the two shapes a diagnostic message is actually
    /// built from — <c>"prefix " ++ x</c> concatenation and <c>@typeName(T)</c> — so the type that
    /// failed appears in the error the way the author wrote it. Null when some part is not readable,
    /// so the caller substitutes rather than throws a second, less useful error.</summary>
    private string? ComptimeMessageText(Item item)
    {
        switch (item.Content)
        {
            case Zig.Grouped g:
                return ComptimeMessageText(g.Arg1);
            case Zig.Concat c:
                return ComptimeMessageText(c.Arg0) is { } left && ComptimeMessageText(c.Arg2) is { } right
                    ? left + right
                    : null;
            case Zig.BuiltinCall b when Tok(b.Arg0) == "@typeName":
            {
                var nameArgs = Flatten(b.Arg2);
                return nameArgs.Count == 1 ? DiagnosticTypeName(nameArgs[0]) : null;
            }
            default:
                return ComptimeStringArg(item);
        }
    }

    /// <summary>Name a type for a DIAGNOSTIC — <c>@compileError("unsupported type: " ++ @typeName(T))</c>,
    /// where naming the offending type is the whole value of the message. Deliberately more lenient
    /// than the <c>@typeName</c> VALUE path (<see cref="ZigTypeSpelling"/>), which refuses an
    /// unspellable type because emitting a wrong name into the program would be a real defect: here the
    /// name is prose, and an approximate one beats losing the author's message. The source spelling
    /// wins when there is one; a <c>comptime T: type</c> is named from its resolved type, honouring the
    /// declared width so a <c>u21</c> instantiation says <c>u21</c> rather than the <c>u32</c> it
    /// widened to (the <see cref="MangleTypeSeed"/> rule, for the same reason).</summary>
    private string DiagnosticTypeName(Item typeAst)
    {
        if (ZigTypeSpelling(typeAst) is { } spelled) { return spelled; }
        var type = LowerType(typeAst).Unqualified;
        if (DeclaredBitsOfTypeArg(typeAst) is { } bits
            && type is CType.Prim { Integer: true, Name: not "_Bool" } p)
        {
            return (p.Signed ? "i" : "u") + bits.ToString(CultureInfo.InvariantCulture);
        }
        return MangleType(type);
    }

    /// <summary>Lower <c>@compileLog(…)</c>. Zig prints its arguments at compile time AND fails the
    /// build ("a compilation error is added to the build, pointing to the compile log statement", so
    /// a log left in a codebase cannot be missed) — so a loud error carrying the logged text is the
    /// faithful lowering, not a leniency.</summary>
    private CExpr CompileLogBuiltin(IReadOnlyList<Item> args)
    {
        var parts = new List<string>(args.Count);
        foreach (var arg in args) { parts.Add(ComptimeMessageText(arg) ?? "?"); }
        throw new IrUnsupportedException(
            "zig `@compileLog(" + string.Join(", ", parts) + ")`: a compile log fails the build in zig too — "
            + "remove it once the value has been read");
    }

    /// <summary>Lower <c>@setEvalBranchQuota(n)</c> — raise the comptime evaluation budget, never
    /// lower it, which is zig's own rule ("if the new_quota is smaller than the default quota or a
    /// previously explicitly set quota, it is ignored").
    ///
    /// <para>The units differ and that is fine in this direction: zig counts BACKWARD BRANCHES, dotcc
    /// counts eval STEPS — a strictly finer unit, so a quota that suffices in zig always suffices here
    /// when taken as a floor. dotcc's default budget is already far above the 2,000–100,000 the pinned
    /// std asks for, so in practice every call is honored and none of them changes anything; what
    /// changes is that 80 uses stop being a loud cut. Yields <c>void</c> in zig, so it is a STATEMENT
    /// (see the <see cref="LowerStmt"/> case) and emits nothing.</para></summary>
    private void SetEvalBranchQuota(IReadOnlyList<Item> args)
    {
        if (args.Count != 1)
        {
            throw new IrUnsupportedException($"zig `@setEvalBranchQuota` expects (quota); got {args.Count} argument(s)");
        }
        var quota = ComptimeBitCount(args[0], "@setEvalBranchQuota");
        if (quota > _evalStepBudget)
        {
            _evalStepBudget = quota;
            _ir.ComptimeStepBudget = quota;
        }
    }

    // ---- the poisoned declaration -----------------------------------------

    /// <summary>Raise the diagnostic a poisoned name carries, if it is one. Consulted where an
    /// ordinary resolution has already missed — a value reference and a type reference both — so a
    /// tombstone (<c>pub const MACH_PORT_RIGHT = @compileError("use MACH.PORT.RIGHT");</c>) is inert
    /// until something names it, exactly as zig's lazy declaration analysis makes it.</summary>
    private void RaiseIfPoisoned(string name)
    {
        if (_poisonedConsts.TryGetValue(name, out var message))
        {
            throw new IrUnsupportedException($"zig `{name}` is declared as `@compileError`: " + message);
        }
        if (_failedContainers.TryGetValue(name, out var failure))
        {
            throw new ZigFailedContainerException(name, failure);
        }
    }

    /// <summary>A reference to a lazy module's container whose registration failed (see
    /// <see cref="_failedContainers"/>). Its own type, so the one position that can do without the
    /// container's layout, the pointee of a single-item pointer (<see cref="LowerPointee"/>), tells it
    /// apart from every other unsupported construct without reading the message.</summary>
    private sealed class ZigFailedContainerException(string container, string failure)
        : IrUnsupportedException($"zig container `{container}` could not be lowered: " + failure);

    /// <summary>Each container of a LAZY module whose registration failed (road-to-zig-std G3), by its
    /// IR and its source name → the failure. Preparing a module registers all of its containers up front,
    /// but zig analyses one only when something names it: <c>std.Io</c>'s <c>Limit</c> enum has a member
    /// <c>unlimited = math.maxInt(usize)</c>, which needs the comptime engine, and a program writing to a
    /// <c>std.Io.Writer</c> never touches it. So the failure is recorded and raised at the first
    /// REFERENCE, the tombstone rule above applied to a container. A root unit stays eager: its own
    /// containers are the program, and one that cannot lower is an error where it stands.</summary>
    private readonly Dictionary<string, string> _failedContainers = new(System.StringComparer.Ordinal);

    /// <summary>After a lazy module's containers are registered, carry each failure to the containers that
    /// depend on it (road-to-zig-std G3). Registration runs in declaration order, so a container can
    /// register BEFORE one it embeds fails: std's <c>File.Reader</c> holds <c>file: File</c>, and the
    /// file-as-struct <c>File</c> fails last, on <c>handle: std.posix.fd_t</c>. To a fixpoint, a container
    /// holding a failed one BY VALUE (a field, an array or optional of one, a by-value fn-pointer
    /// parameter) fails too and is withdrawn from the program; a POINTER to a failed container becomes an
    /// opaque <c>void*</c>, as <see cref="LowerPointee"/> makes it for a pointer lowered after the
    /// failure.</summary>
    private void FailDependentContainers(IEnumerable<string> names)
    {
        var candidates = names.Distinct().ToList();
        bool changed;
        do
        {
            changed = false;
            foreach (var name in candidates)
            {
                if (_failedContainers.ContainsKey(name)) { continue; }
                foreach (var agg in AggregatesOf(name))
                {
                    if (!_ir.StructFields.TryGetValue(agg, out var fields)) { continue; }
                    var badField = fields.FirstOrDefault(f => WithoutFailedContainers(f.Type) is null);
                    if (badField.Name is not { } bad || FailedContainerIn(badField.Type) is not { } dep) { continue; }
                    var message = $"zig container `{name}` could not be lowered: its field `{bad}` holds `{dep}` by value, "
                                  + $"and zig container `{dep}` could not be lowered: " + _failedContainers[dep];
                    foreach (var a in AggregatesOf(name)) { _ir.WithdrawStructType(a); }
                    var aliases = _containerTypes.Where(kv => kv.Value is CType.Named cn && cn.Name == name)
                                                 .Select(kv => kv.Key).ToList();
                    foreach (var n in aliases)
                    {
                        _containerTypes.Remove(n);
                        _failedContainers[n] = message;
                    }
                    _failedContainers[name] = message;
                    changed = true;
                    break;
                }
            }
        }
        while (changed);
        // What survives may still POINT at a failed container: retype those fields to an opaque pointer.
        foreach (var name in candidates.Where(n => !_failedContainers.ContainsKey(n)))
        {
            foreach (var agg in AggregatesOf(name))
            {
                if (!_ir.StructFields.TryGetValue(agg, out var fields)) { continue; }
                var rewritten = fields.Select(f => f with { Type = WithoutFailedContainers(f.Type) ?? f.Type }).ToList();
                if (!rewritten.SequenceEqual(fields)) { _ir.ReplaceStructFields(agg, rewritten); }
            }
        }
    }

    /// <summary>The emitted aggregates a container owns: its own struct and, for a tagged union, its
    /// payload union.</summary>
    private IEnumerable<string> AggregatesOf(string name)
    {
        yield return name;
        if (_unions.TryGetValue(name, out var u) && u.PayloadTypeName is { } payload) { yield return payload; }
    }

    /// <summary><paramref name="t"/> with every pointer to a failed container (see
    /// <see cref="_failedContainers"/>) made opaque (<c>void*</c>, qualifiers kept), or null when it holds
    /// a failed container BY VALUE, which no rewrite can express.</summary>
    private CType? WithoutFailedContainers(CType t)
    {
        switch (t)
        {
            case CType.Named n:
                return _failedContainers.ContainsKey(n.Name) ? null : t;
            case CType.Pointer { Pointee: CType.Named pn } p when _failedContainers.ContainsKey(pn.Name):
                return p with { Pointee = CType.Void.WithQuals(pn.Quals) };
            case CType.Pointer p:
                return WithoutFailedContainers(p.Pointee) is { } pte ? p with { Pointee = pte } : null;
            case CType.Array a:
                return WithoutFailedContainers(a.Element) is { } ae ? a with { Element = ae } : null;
            case CType.Optional o:
                return WithoutFailedContainers(o.Inner) is { } oi ? o with { Inner = oi } : null;
            case CType.Slice s:
                return WithoutFailedContainers(s.Element) is { } se ? s with { Element = se } : null;
            case CType.ErrorUnion eu:
                return WithoutFailedContainers(eu.Payload) is { } ep ? eu with { Payload = ep } : null;
            // A curated list's element is a C# generic argument (`ZigList<T>`): a failed one fails the holder, as a by-value
            // field does (std.Io.Dir.SelectiveWalker's `stack: std.ArrayList(StackItem)`).
            case CType.ZigList zl:
                return WithoutFailedContainers(zl.Element) is { } le && FailedContainerIn(zl.Element) is null ? zl with { Element = le } : null;
            case CType.Func f:
            {
                if (WithoutFailedContainers(f.Return) is not { } r) { return null; }
                var ps = new List<CType>(f.Params.Count);
                foreach (var p in f.Params)
                {
                    if (WithoutFailedContainers(p) is not { } fp) { return null; }
                    ps.Add(fp);
                }
                return f with { Return = r, Params = ps };
            }
            default:
                return t;
        }
    }

    /// <summary>The first failed container <paramref name="t"/> holds by value (the reason
    /// <see cref="WithoutFailedContainers"/> returned null), for the diagnostic.</summary>
    private string? FailedContainerIn(CType t) => t switch
    {
        CType.Named n => _failedContainers.ContainsKey(n.Name) ? n.Name : null,
        CType.Pointer { Pointee: CType.Named pn } when _failedContainers.ContainsKey(pn.Name) => null,
        CType.Pointer p => FailedContainerIn(p.Pointee),
        CType.Array a => FailedContainerIn(a.Element),
        CType.Optional o => FailedContainerIn(o.Inner),
        CType.Slice s => FailedContainerIn(s.Element),
        CType.ErrorUnion eu => FailedContainerIn(eu.Payload),
        CType.ZigList zl => zl.Element is CType.Named ln && _failedContainers.ContainsKey(ln.Name) ? ln.Name : FailedContainerIn(zl.Element),
        CType.Func f => FailedContainerIn(f.Return) ?? f.Params.Select(FailedContainerIn).FirstOrDefault(x => x is not null),
        _ => null,
    };

    /// <summary>Run one container's pass-0 registration, isolating a failure in a LAZY module (see
    /// <see cref="_failedContainers"/>): the container is withdrawn from the type table, so nothing
    /// resolves a half-registered type, and its names raise the recorded message when referenced.
    /// Returns false when the registration failed.</summary>
    private bool RegisterContainerIsolated(string name, string? plainName, System.Action register)
    {
        if (!_lazy) { register(); return true; }
        try
        {
            register();
            return true;
        }
        catch (IrUnsupportedException ex)
        {
            foreach (var n in new[] { name, plainName ?? name })
            {
                _containerTypes.Remove(n);
                _failedContainers[n] = ex.Message;
            }
            return false;
        }
    }
}
