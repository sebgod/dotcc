#nullable enable

using System.Collections.Generic;
using System.Linq;
using DotCC.Ir;

namespace DotCC.Backends;

/// <summary>
/// Which functions need an unsafe context. A C function becomes a C# method marked <c>unsafe</c> only when it uses
/// what C# allows only there: a pointer, an address, an array (a pointer or a fixed buffer here), a string literal
/// (a <c>byte*</c>), varargs, and the like. A function over plain values (<c>int factorial(int n)</c>) is emitted
/// without it. The test is a whitelist over the IR: every type the function touches must be a pointer-free scalar,
/// enum or struct, and every node one of the plain kinds below; anything else keeps <c>unsafe</c>. So a node this
/// does not know stays unsafe, and a miss could only be a C# compile error (CS0214), never a different program:
/// <c>unsafe</c> changes no IL.
/// </summary>
internal sealed partial class CSharpBackend
{
    /// <summary>Each struct or union, by name, whether its fields are all safe (<see cref="IsSafeStruct"/>).</summary>
    private readonly Dictionary<string, bool> _safeStructs = new(System.StringComparer.Ordinal);

    /// <summary>True when <paramref name="fn"/> must be an <c>unsafe</c> method (see the class summary).</summary>
    private bool NeedsUnsafe(FuncDef fn)
    {
        if (fn.Variadic) { return true; }
        if (fn.Sym.Type is not CType.Func f || !IsSafeType(f.Return)) { return true; }
        return fn.Params.Any(p => !IsSafeType(p.Type)) || !fn.Body.Stmts.All(IsSafeStmt);
    }

    /// <summary>A type a safe method may hold: an integer, floating, boolean or complex scalar, <c>void</c>, an enum,
    /// or a struct or union whose fields are (<see cref="IsSafeStruct"/>).</summary>
    private bool IsSafeType(CType t) => t.Unqualified switch
    {
        CType.Prim => true,
        CType.VoidType => true,
        CType.Enum => true,
        CType.ComplexType => true,
        CType.Named n => IsSafeStruct(n.Name),
        _ => false,
    };

    /// <summary>A struct or union of the module whose fields are all safe types and none an array (a fixed buffer or
    /// an inline-array wrapper). An unknown name (an opaque type) or a runtime-owned one is not.</summary>
    private bool IsSafeStruct(string name)
    {
        if (_safeStructs.TryGetValue(name, out var known)) { return known; }
        _safeStructs[name] = false;   // a cycle reaches it only through a pointer, which is unsafe anyway
        var def = _module?.Types.FirstOrDefault(t => t.Name == name);
        var safe = def is { IsRuntimeOwned: false }
            && def.Fields.All(fd => fd.Type.Unqualified is not CType.Array && IsSafeType(fd.Type));
        _safeStructs[name] = safe;
        return safe;
    }

    /// <summary>A statement of the plain kinds whose parts are all safe.</summary>
    private bool IsSafeStmt(CStmt s) => s switch
    {
        Block b => b.Stmts.All(IsSafeStmt),
        Seq q => q.Stmts.All(IsSafeStmt),
        DeclStmt d => d.Decls.All(ld => IsSafeType(ld.Sym.Type) && (ld.Init is null || IsSafeExpr(ld.Init))),
        ExprStmt es => IsSafeExpr(es.Expr),
        If i => IsSafeExpr(i.Cond) && IsSafeStmt(i.Then) && (i.Else is null || IsSafeStmt(i.Else)),
        While w => IsSafeExpr(w.Cond) && IsSafeStmt(w.Body),
        DoWhile dw => IsSafeStmt(dw.Body) && IsSafeExpr(dw.Cond),
        For fo => (fo.Init is null || IsSafeStmt(fo.Init)) && (fo.Cond is null || IsSafeExpr(fo.Cond))
                  && (fo.Post is null || IsSafeExpr(fo.Post)) && IsSafeStmt(fo.Body),
        Return r => r.Value is null || IsSafeExpr(r.Value),
        Break or Continue or Goto or FallthroughMarker => true,
        Labeled l => IsSafeStmt(l.Body),
        Switch sw => IsSafeExpr(sw.Subject) && sw.Sections.All(sec => sec.Body.All(IsSafeStmt)),
        CaseLabelStmt cl => (cl.CaseExpr is null || IsSafeExpr(cl.CaseExpr)) && IsSafeStmt(cl.Body),
        _ => false,
    };

    /// <summary>An expression of a safe type, of the plain kinds whose parts are all safe: literals, variables,
    /// operators other than <c>&amp;</c> and <c>*</c>, casts, conditionals, field access through a value, a struct
    /// value, and a direct call whose signature is safe.</summary>
    private bool IsSafeExpr(CExpr e) => IsSafeType(e.Type) && e switch
    {
        LitInt or LitBool or LitFloat or EnumConstRef or DefaultLit => true,
        VarRef v => IsSafeType(v.Sym.Type),
        Unary u => u.Op is not (UnOp.AddrOf or UnOp.Deref) && IsSafeExpr(u.Operand),
        Binary b => IsSafeExpr(b.Left) && IsSafeExpr(b.Right),
        Assign a => IsSafeExpr(a.Target) && IsSafeExpr(a.Value),
        Cast c => IsSafeExpr(c.Operand),
        CondExpr ce => IsSafeExpr(ce.Cond) && IsSafeExpr(ce.Then) && IsSafeExpr(ce.Else),
        Paren p => IsSafeExpr(p.Inner),
        CommaOp co => co.Items.All(IsSafeExpr),
        CommaSeq cs => cs.Items.All(IsSafeExpr),
        Member m => !m.Arrow && IsSafeExpr(m.Base),
        StructInit si => si.Members.All(fi => IsSafeType(fi.FieldType) && IsSafeExpr(fi.Value)),
        // `printf("…", x, "s")`: the literal format and any literal string argument go over as spans
        // (Libc.printf(ReadOnlySpan<byte>), PrintfBuilder.Arg(ReadOnlySpan<byte>)); the other arguments are safe when
        // their values are.
        Call { Callee: "printf" } pc when LiteralFormat(pc) is not null
            => pc.Args.Skip(1).All(arg => LiteralString(arg) is not null || IsSafeExpr(arg)),
        Call c => c.Args.All(IsSafeExpr) && (c.ParamTypes is null || c.ParamTypes.All(IsSafeType))
                  && (c.CalleeSym is null || c.CalleeSym.Type is CType.Func { Variadic: false } cf
                      && cf.Params.All(IsSafeType) && IsSafeType(cf.Return)),
        _ => false,
    };
}
