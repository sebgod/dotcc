#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using LALR.CC.LexicalGrammar;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // Block / statements
    public EmitContent Visit(C.Block n)
    {
        // C90 forbids a declaration after a statement in a block (mixed
        // declarations and code is C99). The StmtList reductions below set
        // _blkOutOfOrder for this block; report it once here. n.Arg0 is `{`,
        // Arg1 is the ScopeEnter marker, Arg2 the StmtList.
        if (_dialectGate is not null && _blkOutOfOrder)
        {
            Gate(1999, "mixed declarations and statements", n.Arg0);
        }
        var body = "{\n" + IndentEach(T(n.Arg2)) + "}\n";
        PopScope();  // close the frame ScopeEnter opened after `{`
        return body;
    }
    public EmitContent Visit(C.BlockEmpty n) { PopScope(); return "{ }\n"; }
    // StmtList builds right-to-left (Arg0 = this statement, Arg1 = the rest).
    // Track, for the C90 mixed-declarations gate, whether the sub-list holds a
    // declaration and whether a declaration follows a non-declaration in it.
    public EmitContent Visit(C.StmtsCons n)
    {
        if (_dialectGate is not null)
        {
            var thisIsDecl = n.Arg0.Content is EmitContent.DeclStmtMarker;
            // Arg0 precedes the rest: a non-decl here with a decl later is mixed.
            if (!thisIsDecl && _blkContainsDecl) { _blkOutOfOrder = true; }
            _blkContainsDecl = thisIsDecl || _blkContainsDecl;
        }
        return T(n.Arg0) + T(n.Arg1);
    }
    public EmitContent Visit(C.StmtsOne n)
    {
        if (_dialectGate is not null)
        {
            // Rightmost statement — starts a fresh per-block accumulator.
            _blkContainsDecl = n.Arg0.Content is EmitContent.DeclStmtMarker;
            _blkOutOfOrder = false;
        }
        return T(n.Arg0);
    }
    // Conditions are wrapped with B(...) so int- and pointer-valued conditions
    // (`while (1)`, `if (p)`, `for (...; n; ...)`) typecheck. The B overloads
    // live in BuildShell — see Compiler.BuildShell.
    public EmitContent Visit(C.StmtIf n)
    {
        // setjmp in an `if` without an `else` can't be locally rewritten
        // — there's no "normal path" to put in the try block. Fail with
        // a clear hint; the user should add an else branch.
        if (n.Arg2.Content is EmitContent.SetjmpCall
            or EmitContent.SetjmpCheckZero)
        {
            throw new CompileException(
                "setjmp(env) in an `if` condition requires a matching `else` clause; " +
                "see the supported patterns in <setjmp.h>'s header comment.");
        }
        return $"if ({CondOf(n.Arg2)}) {T(n.Arg4)}";
    }

    public EmitContent Visit(C.StmtIfElse n)
    {
        // Setjmp recognition via AST inspection — no text matching.
        // Visit(Call) for setjmp returns a SetjmpCall variant; Visit(Eq)/
        // Visit(Neq) escalate to SetjmpCheckZero when one operand is
        // setjmp and the other is the literal 0. Both shapes get the
        // try/catch rewrite here.
        switch (n.Arg2.Content)
        {
            case EmitContent.SetjmpCall sj:
                // `if (setjmp(env))         { recovery } else { normal }`
                //   setjmp is truthy ONLY on the longjmp re-entry, so:
                //     then-branch = recovery (catch body)
                //     else-branch = normal   (try body)
                return EmitSetjmpRewrite(sj.EnvName, tryBody: T(n.Arg6), catchBody: T(n.Arg4));

            case EmitContent.SetjmpCheckZero scz:
                // `if (setjmp(env) == 0)    { normal }   else { recovery }`
                //   TruthyOnFirstCall=true: then-branch = normal, else = recovery.
                // `if (setjmp(env) != 0)    { recovery } else { normal }`
                //   TruthyOnFirstCall=false: then-branch = recovery, else = normal.
                var (tryBody, catchBody) = scz.TruthyOnFirstCall
                    ? (T(n.Arg4), T(n.Arg6))
                    : (T(n.Arg6), T(n.Arg4));
                return EmitSetjmpRewrite(scz.EnvName, tryBody, catchBody);

            default:
                return $"if ({CondOf(n.Arg2)}) {T(n.Arg4)}else {T(n.Arg6)}";
        }
    }

    /// <summary>
    /// Emit the <c>try / catch when</c> shape that lowers a recognised
    /// <c>setjmp/longjmp</c> idiom. The <c>when</c> filter matches on
    /// the env-token identity so nested setjmps stay disambiguated.
    /// </summary>
    private static string EmitSetjmpRewrite(string envName, string tryBody, string catchBody) =>
        $"try {tryBody}" +
        $"catch (Libc.LongJmpException __jmp) when (__jmp.Token == {envName}) " +
        $"{{ var __longjmp_value = __jmp.Value; {catchBody} }}";
    public EmitContent Visit(C.StmtWhile n) => $"while ({CondOf(n.Arg2)}) {T(n.Arg4)}";

    // `do Stmt while (E) ;` — body runs at least once. C# accepts the same
    // shape; only the condition needs Cond.B wrapping. Note the trailing
    // semicolon is required in both C and C#.
    public EmitContent Visit(C.StmtDoWhile n) =>
        $"do {T(n.Arg1)}while ({CondOf(n.Arg4)});\n";

    // `for (Decl; E; E) Stmt` — emit C#'s for verbatim. C# accepts the same
    // shape; the init declaration scopes to the loop body. The init Decl
    // here is the LHS (`int i = 0` form); the StripOuterParens on the
    // incr keeps the emitter from wrapping `i++` in extra parens that C#
    // rejects in for-clause position.
    // for-loop clauses. ForCond produces the already-Cond.B-wrapped condition
    // (or C# `true` when empty); ForPost the update text (or empty). So the
    // Stmt productions just splice the pieces — no CondOf here.
    public EmitContent Visit(C.ForCondExpr n)  => CondOf(n.Arg0);
    public EmitContent Visit(C.ForCondEmpty n) => "true";
    public EmitContent Visit(C.ForPostExprs n) => T(n.Arg0);
    public EmitContent Visit(C.ForPostEmpty n) => "";
    public EmitContent Visit(C.StmtForDecl n)
    {
        // ScopeEnter (Arg2) opened the for-init frame after `(`; indices below
        // are shifted by one accordingly. Decl=Arg3, ForCond=Arg5, ForPost=Arg7,
        // body=Arg9. Pop the frame once the whole statement is built.
        Gate(1999, "declaration in `for` initializer", n.Arg0);  // C99
        var s = $"for ({T(n.Arg3)}; {T(n.Arg5)}; {T(n.Arg7)}) {T(n.Arg9)}";
        PopScope();
        return s;
    }
    public EmitContent Visit(C.StmtForExpr n) =>
        $"for ({T(n.Arg2)}; {T(n.Arg4)}; {T(n.Arg6)}) {T(n.Arg8)}";
    public EmitContent Visit(C.StmtForNoInit n) =>
        $"for (; {T(n.Arg3)}; {T(n.Arg5)}) {T(n.Arg7)}";

    // Comma-separated expression list used in for-init / for-update.
    // C# accepts `for (i=0, j=10; …; i++, j--)` natively, so we just
    // splice the expressions together with `, ` between them — no
    // helper, no parens, no special lowering. The single-expression
    // form passes through unchanged so for-loops with a lone init or
    // update still emit identically to before.
    public EmitContent Visit(C.CommaExprOne n) => StripOuterParens(T(n.Arg0));
    public EmitContent Visit(C.CommaExprCons n) => $"{T(n.Arg0)}, {StripOuterParens(T(n.Arg2))}";
    public EmitContent Visit(C.StmtReturn n)
    {
        // Reconcile the returned value's enum-ness with the declared return type
        // (same rule as a decl/assignment sink): enum fn ← non-matching value
        // gets `(Enum)`, non-enum fn ← enum value decays to `(int)`.
        var text = T(n.Arg1);
        var exprEnum = EnumOf(n.Arg1);
        if (_currentFunctionReturnType is { } ret)
        {
            if (_enumTags.Contains(ret)) { if (exprEnum != ret) { text = $"({ret})({text})"; } }
            else if (exprEnum is not null) { text = $"(int)({text})"; }
        }
        return $"return {text};\n";
    }
    public EmitContent Visit(C.StmtReturnVoid n) => "return;\n";
    public EmitContent Visit(C.StmtBreak n) => "break;\n";
    public EmitContent Visit(C.StmtContinue n) => "continue;\n";

    // switch (E) Block — switch body is a plain Block. `case X:` and
    // `default:` are statement-level labels (see CaseLabel/DefaultLabel)
    // that can appear anywhere inside the Block — including nested inside
    // a do-while or other control flow, enabling Duff's-device-shaped code.
    // C# accepts the same shape.
    public EmitContent Visit(C.StmtSwitch n) =>
        $"switch ({T(n.Arg2)}) {T(n.Arg4)}";

    // Statement-level case/default labels. Body is a single Stmt (which
    // may itself be another labeled stmt — `case 1: case 2: do_thing();`
    // chains naturally).
    public EmitContent Visit(C.CaseLabel n) =>
        $"case {T(n.Arg1)}:\n{T(n.Arg3)}";
    public EmitContent Visit(C.DefaultLabel n) =>
        $"default:\n{T(n.Arg2)}";
    // Tagged as a declaration statement so the C90 mixed-declarations gate can
    // distinguish it from a non-declaration in the enclosing block. Renders to
    // the same text everywhere (T() unwraps the marker).
    public EmitContent Visit(C.StmtDecl n) => new EmitContent.DeclStmtMarker($"{T(n.Arg0)};\n");

    // Block-scope `static T x [= C];`. Each declarator becomes a mangled
    // `public static unsafe` field in DotCcGlobals (static storage duration,
    // persists across calls, initialised once — all native to a C# static
    // field), and we register name→mangled so Visit(Var) rewrites in-function
    // uses. The statement itself emits nothing into the function body — the
    // storage lives in the globals class, not as a local. C requires static
    // initialisers to be constant expressions, so the field initialiser (which
    // also runs exactly once) is an exact match.
    public EmitContent Visit(C.StmtStaticDecl n)
    {
        var type = T(n.Arg1);
        var entries = DE(n.Arg2);
        var fn = _currentFunctionName ?? "fn";
        var mangled = new List<EmitContent.DeclEntry>(entries.Count);
        foreach (var e in entries)
        {
            var name = $"__static_{fn}_{e.Name}";
            _fnStatics[e.Name] = name;
            mangled.Add(new EmitContent.DeclEntry(name, e.Init));
        }
        EmitGlobalFields(type, mangled);
        return string.Empty;
    }

    // `goto label;` — C# accepts the same keyword + identifier syntax with
    // identical forward-reference semantics inside a method body, so the
    // lowering is verbatim.
    public EmitContent Visit(C.StmtGoto n) => $"goto {Id(T(n.Arg1))};\n";

    // `label: Stmt` — emit the label followed by the body statement.
    // Whitespace shape: label on its own line for readability.
    public EmitContent Visit(C.StmtLabel n) =>
        $"{Id(T(n.Arg0))}:\n{T(n.Arg2)}";

    // Empty statement `;` — required pre-C23 if you want to label the end
    // of a block (`end: ;`). Emit as a bare semicolon; C# parses it as an
    // empty statement too.
    public EmitContent Visit(C.StmtEmpty n) => ";\n";
    public EmitContent Visit(C.StmtExpr n)
    {
        // Comma operator as a statement (`a, b, c;`): the result is discarded
        // and C has a sequence point at each comma, so emit each operand as its
        // own statement. (C# has no comma operator, and `(a, b).Item2;` isn't a
        // valid statement-expression — CS0201 — so the tuple form can't be used
        // here.) An operand that isn't a valid C# statement-expression (a bare
        // value with no side effect) reaches Roslyn as CS0201 — the same loud
        // failure C# gives such pointless code.
        if (n.Arg0.Content is EmitContent.CommaSeq seq)
        {
            var sb = new StringBuilder();
            foreach (var op in seq.Operands) { sb.Append(StripOuterParens(op)).Append(";\n"); }
            return sb.ToString();
        }
        // CS0201: bare parenthesized assignment isn't a statement. Peel the
        // outer parens that our binop emitters wrap on.
        return $"{StripOuterParens(T(n.Arg0))};\n";
    }

}
