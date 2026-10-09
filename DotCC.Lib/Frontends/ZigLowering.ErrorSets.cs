#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Error literals and error sets: <c>error.Name</c>, a set-qualified <c>E.Name</c>, and the checks that a
/// returned error belongs to the function's declared set. One concern of the <see cref="ZigLowering"/>
/// binder.</summary>
internal sealed partial class ZigLowering
{
    /// <summary>True when an item is an <c>error.Foo</c> literal, yielding the error name.</summary>
    private static bool IsErrorLit(Item it, out string name)
    {
        if (it.Content is Zig.ErrorLit e) { name = Tok(e.Arg2); return true; }
        name = "";
        return false;
    }
    /// <summary>True when an item is a set-qualified error reference <c>E.member</c> (Milestone X,
    /// part 2) — a <see cref="Zig.Field"/> whose base names a registered <c>error{…}</c> set —
    /// yielding the member name. dotcc erases set membership, so <c>E.member</c> resolves to the same
    /// flat code as the bare <c>error.member</c> (real zig: the same global error value). Recognized
    /// wherever <see cref="IsErrorLit"/> is — the value path and the <see cref="LowerReturn"/> error
    /// return. Instance (not static like <see cref="IsErrorLit"/>) because it reads <c>_errorSets</c>.</summary>
    private bool TryErrorSetMember(Item it, out string set, out string name)
    {
        if (it.Content is Zig.Field f && f.Arg0.Content is Zig.Ident id && _errorSets.Contains(Tok(id.Arg0)))
        {
            set = Tok(id.Arg0);
            name = Tok(f.Arg2);
            return true;
        }
        set = "";
        name = "";
        return false;
    }
    /// <summary>Reject a set-qualified <c>E.member</c> whose member is not declared in set <c>E</c>
    /// (Milestone X, part 3) — an illegal program real zig rejects, so dotcc does too (a good compiler
    /// rejects illegal programs). Lenient only if <c>E</c> somehow has no recorded members.</summary>
    private void ValidateSetMember(string set, string member)
    {
        if (_errorSetMembers.TryGetValue(set, out var members) && !members.Contains(member))
        {
            throw new CompileException($"zig: error '{member}' is not a member of error set '{set}'");
        }
    }
    /// <summary>Reject a directly-returned error (<c>return error.X;</c> / <c>return E.X;</c>) whose
    /// name is outside the current function's DECLARED error set (Milestone X, part 3) — e.g.
    /// <c>fn f() error{A}!u8 { return error.B; }</c>. No-op when the function is unconstrained (an
    /// inferred <c>!T</c> / <c>anyerror!T</c>, <see cref="_currentFnErrorSet"/> null). V1 checks the
    /// direct-return forms only; an error that flows in through a CALL or <c>try</c> is not yet
    /// set-checked (a documented cut — it would need cross-function set inference).</summary>
    private void CheckReturnedErrorInSet(string errName)
    {
        if (_currentFnErrorSet is { } cs && !cs.members.Contains(errName))
        {
            var which = cs.name is { } n ? $"error set '{n}'" : "the function's declared error set";
            throw new CompileException(
                $"zig: error '{errName}' is not a member of {which} (the function's return-error set)");
        }
    }
    /// <summary>A function's DECLARED error set, for the foreign-error return check (Milestone X,
    /// part 3). Returns false (UNCONSTRAINED — any error is accepted) for an inferred bare <c>!T</c>,
    /// for <c>anyerror!T</c>, or for an unknown set name (real zig infers / widens those); returns true
    /// with the allowed member names for an <c>E!T</c> over a declared set or an inline
    /// <c>error{…}!T</c>.</summary>
    private bool TryDeclaredErrorSet(Item retType, bool errUnion, out string? setName, out HashSet<string> members)
    {
        setName = null;
        members = new HashSet<string>(System.StringComparer.Ordinal);
        if (errUnion) { return false; }                          // bare `!T` — inferred set
        if (retType.Content is not Zig.ErrUnion eu) { return false; }
        switch (eu.Arg0.Content)
        {
            case Zig.Ident id when Tok(id.Arg0) != "anyerror" && _errorSetMembers.TryGetValue(Tok(id.Arg0), out var declared):
                setName = Tok(id.Arg0);
                members = declared;
                return true;
            case Zig.ErrorSet inlineSet:   // `error{}!T` (no members: never errors) too
                foreach (var m in WalkErrSetMembers(inlineSet.Arg2)) { members.Add(m); }
                return true;
            default:
                return false;                                    // anyerror / an unknown set name
        }
    }
    /// <summary>Lower a bare <c>error.Foo</c> value to its stable code in the flat global error set,
    /// typed <see cref="CType.ErrorSet"/> (rendered <c>ushort</c>). The code IS the value, so
    /// error-value equality compares codes (<c>e == error.Foo</c> → <c>e == &lt;code&gt;</c>). Shared
    /// by the bare-value lowering (here) and the captured-error binding; <c>return error.Foo;</c>
    /// keeps its dedicated <see cref="ErrUnionErr"/> / <see cref="ZigErrorThrow"/> path in
    /// <see cref="LowerReturn"/>.</summary>
    private CExpr LowerErrorLit(string name)
    {
        var code = ErrorCode(name);
        return new LitInt(code.ToString(CultureInfo.InvariantCulture), code) { Type = CType.ErrorSet };
    }
}
