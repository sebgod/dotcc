#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        // The StmtList reduces to a structured StmtSeq (pieces not yet joined) so a
        // switch body can group case sections and insert fall-through jumps. Join
        // the pieces here for the ordinary block; carry the pieces (+ whether the
        // block's last statement terminates) so an enclosing switch / block can use
        // them.
        var seq = (EmitContent.StmtSeq)n.Arg2.Content;
        var bodyText = string.Concat(seq.Pieces.Select(p => p.Text));
        var body = "{\n" + IndentEach(bodyText) + "}\n";
        PopScope();  // close the frame ScopeEnter opened after `{`
        return new EmitContent.Text(body,
            Terminates: seq.Pieces.Count > 0 && seq.Pieces[^1].Terminates,
            BlockPieces: seq.Pieces);
    }
    public EmitContent Visit(C.BlockEmpty n) { PopScope(); return "{ }\n"; }
    // StmtList builds right-to-left (Arg0 = this statement, Arg1 = the rest), so
    // the tail is fully reduced when each cons fires. Accumulate a structured
    // StmtSeq of pieces (text + control-flow facts) rather than joining to text —
    // the switch fall-through analysis needs the per-statement structure. Also
    // track, for the C90 mixed-declarations gate, whether the sub-list holds a
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
        var rest = (EmitContent.StmtSeq)n.Arg1.Content;
        var pieces = new List<EmitContent.StmtPiece>(rest.Pieces.Count + 1) { PieceOf(n.Arg0) };
        pieces.AddRange(rest.Pieces);
        // The sequence's last piece is the tail's (head is prepended), so the
        // whole list terminates iff the tail does.
        return new EmitContent.StmtSeq(pieces, rest.Terminates);
    }
    public EmitContent Visit(C.StmtsOne n)
    {
        if (_dialectGate is not null)
        {
            // Rightmost statement — starts a fresh per-block accumulator.
            _blkContainsDecl = n.Arg0.Content is EmitContent.DeclStmtMarker;
            _blkOutOfOrder = false;
        }
        var piece = PieceOf(n.Arg0);
        return new EmitContent.StmtSeq(new[] { piece }, piece.Terminates);
    }

    // Build a StmtPiece from one statement: its emitted text plus the control-flow
    // facts (terminates? is it a case/default label?) the switch analysis reads.
    // A statement that isn't a tagged Text (a DeclStmtMarker, a bare string) is a
    // plain, non-terminating, non-label piece.
    private EmitContent.StmtPiece PieceOf(Item it) =>
        it.Content is EmitContent.Text t
            ? new EmitContent.StmtPiece(T(it), t.Terminates, t.CaseLabelExpr, t.IsDefaultLabel)
            : new EmitContent.StmtPiece(T(it));

    // True when a statement provably ends control flow at its end.
    private static bool TerminatesOf(Item it) => it.Content is EmitContent.Text { Terminates: true };
    // Conditions are wrapped with B(...) so int- and pointer-valued conditions
    // (`while (1)`, `if (p)`, `for (...; n; ...)`) typecheck. The B overloads
    // live in BuildShell — see Compiler.BuildShell.
    public EmitContent Visit(C.StmtIf n)
    {
        // setjmp in an `if` WITHOUT an `else` — the missing branch is simply
        // empty. The try/catch rewrite still applies; the absent side becomes an
        // empty block. This is Lua's `LUAI_TRY` shape: `if (setjmp((c)->b) == 0)
        // ((f)(L, ud));` ≡ run f under a longjmp guard, swallow the unwind.
        switch (n.Arg2.Content)
        {
            case EmitContent.SetjmpCall sj:
                // `if (setjmp(env)) recovery;` — setjmp is truthy only on the
                // longjmp re-entry, so the body is recovery and the normal path
                // (the absent else) is empty: try { } catch { recovery }.
                return EmitSetjmpRewrite(sj.EnvName, tryBody: "", catchBody: T(n.Arg4));

            case EmitContent.SetjmpCheckZero scz:
                // `if (setjmp(env) == 0) normal;` → try { normal } catch { }
                // `if (setjmp(env) != 0) recovery;` → try { } catch { recovery }
                var (tryBody, catchBody) = scz.TruthyOnFirstCall
                    ? (T(n.Arg4), "")
                    : ("", T(n.Arg4));
                return EmitSetjmpRewrite(scz.EnvName, tryBody, catchBody);

            default:
                // `if (S1, …, C) THEN` — the controlling expression is a comma.
                // Lift the side-effect operands into a wrapping block, test the
                // last: `{ S1; …; if (Cond.B(C)) THEN }`.
                if (CommaOpsOf(n.Arg2) is { Count: > 1 } ops)
                {
                    return $"{{\n{IndentEach(CommaLeadingStmts(ops))}"
                        + $"if (Cond.B({StripOuterParens(ops[^1])})) {T(n.Arg4)}}}\n";
                }
                return $"if ({CondOf(n.Arg2)}) {T(n.Arg4)}";
        }
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
                // `if (S1, …, C) THEN else ELSE` — comma controlling expr: lift the
                // side effects, test the last (the whole if-else stays in the block).
                if (CommaOpsOf(n.Arg2) is { Count: > 1 } ops)
                {
                    return $"{{\n{IndentEach(CommaLeadingStmts(ops))}"
                        + $"if (Cond.B({StripOuterParens(ops[^1])})) {T(n.Arg4)}else {T(n.Arg6)}}}\n";
                }
                return $"if ({CondOf(n.Arg2)}) {T(n.Arg4)}else {T(n.Arg6)}";
        }
    }

    /// <summary>
    /// Emit the <c>try / catch when</c> shape that lowers a recognised
    /// <c>setjmp/longjmp</c> idiom. The <c>when</c> filter matches on
    /// the env-token identity so nested setjmps stay disambiguated.
    /// </summary>
    private static string EmitSetjmpRewrite(string envName, string tryBody, string catchBody)
    {
        // An empty branch (the no-else `Visit(StmtIf)` case) becomes an empty
        // block. Only bind `__longjmp_value` when there's recovery code that
        // could read it — an empty catch needs no binding (and binding it would
        // leave an unused local).
        // The try body must be a BLOCK — `try stmt;` is invalid C#. When the C
        // then/else branch is a single (unbraced) statement (Lua's `LUAI_TRY` =
        // `if (setjmp((c)->b) == 0) (f)(L, ud);`), wrap it; an already-braced block
        // just nests harmlessly.
        var tb = string.IsNullOrWhiteSpace(tryBody) ? "{ }\n" : $"{{ {tryBody} }}\n";
        var catchInner = string.IsNullOrWhiteSpace(catchBody)
            ? "{ }"
            : $"{{ var __longjmp_value = __jmp.Value; {catchBody} }}";
        // Arm this setjmp site with a FRESH token identity. The token must be
        // unique and non-null so the `when` filter disambiguates nested setjmps:
        // a `longjmp(env, …)` reads the SAME `env` (so it matches here), while a
        // longjmp aimed at a DIFFERENT (e.g. outer / base) env carries a different
        // token and propagates past this catch. Without it, an uninitialised
        // `jmp_buf` field (Lua's `lua_longjmp lj = default;` → `lj.b == null`)
        // makes every filter `Token == null`, so a base-level throw
        // (`luaD_throwbaselevel`, used by `coroutine.close` on a running coroutine)
        // is caught by the NEAREST handler instead of the base — resuming on an
        // already-reset stack and corrupting memory.
        return $"{envName} = new Libc.LongJmpToken();\n" +
            $"try {tb}" +
            $"catch (Libc.LongJmpException __jmp) when (__jmp.Token == {envName}) " +
            catchInner;
    }
    public EmitContent Visit(C.StmtWhile n)
    {
        // `while (S1, …, Sk, C) BODY` — comma controlling expression. C re-evaluates
        // the WHOLE expression each iteration (and on `continue`), so lift the
        // side-effect operands to the top of the body and test the last:
        //   while (true) { S1; …; Sk; if (!Cond.B(C)) break; BODY }
        // A `continue` in BODY jumps to the loop top → re-runs S1…Sk, matching C.
        // This is Lua's llex idiom `while (cast_void(save_and_next(ls)), lisxdigit(…))`.
        if (CommaOpsOf(n.Arg2) is { Count: > 1 } ops)
        {
            return $"while (true) {{\n{IndentEach(CommaLeadingStmts(ops))}"
                + $"if (!Cond.B({StripOuterParens(ops[^1])})) break;\n"
                + $"{IndentEach(T(n.Arg4))}}}\n";
        }
        return $"while ({CondOf(n.Arg2)}) {T(n.Arg4)}";
    }

    // `do Stmt while (E) ;` — body runs at least once. C# accepts the same
    // shape; only the condition needs Cond.B wrapping. Note the trailing
    // semicolon is required in both C and C#.
    public EmitContent Visit(C.StmtDoWhile n)
    {
        // `do BODY while (S1, …, C);` — comma controlling expr. Run the side
        // effects after BODY each iteration, test the last. (Caveat: a `continue`
        // in BODY jumps straight to the C# `while` test, skipping S1…Sk — a rare
        // divergence from C for do-while-comma-with-continue; documented.)
        if (CommaOpsOf(n.Arg4) is { Count: > 1 } ops)
        {
            return $"do {{\n{IndentEach(T(n.Arg1))}{IndentEach(CommaLeadingStmts(ops))}}}"
                + $" while (Cond.B({StripOuterParens(ops[^1])}));\n";
        }
        return $"do {T(n.Arg1)}while ({CondOf(n.Arg4)});\n";
    }

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
        // `return (guard, value);` — a value-context comma needing hoisting: lift
        // the leading statements, then return the value.
        if (n.Arg1.Content is EmitContent.SeqExpr se)
        {
            return new EmitContent.Text(
                $"{{\n{IndentEach(string.Concat(se.LeadingStmts))}return {se.ValueExpr};\n}}\n",
                Terminates: true);
        }
        // Reconcile the returned value's enum-ness with the declared return type
        // (same rule as a decl/assignment sink): enum fn ← non-matching value
        // gets `(Enum)`, non-enum fn ← enum value decays to `(int)`.
        var text = T(n.Arg1);
        var exprEnum = EnumOf(n.Arg1);
        if (_currentFunctionReturnType is { } ret)
        {
            if (_enumTags.Contains(ret)) { if (exprEnum != ret) { text = $"({ret})({text})"; } }
            else if (exprEnum is not null) { text = $"(int)({text})"; }
            // A narrowing / sign-incompatible return into an integer return type
            // takes the C-conversion cast C# requires (-Wconversion flags a
            // width-narrowing). Enum sources already decayed to int above.
            else { text = CoerceStore(text, TyOf(n.Arg1), ConstOfItem(n.Arg1), ret, n.Arg1.Position.Line); }
        }
        return new EmitContent.Text($"return {text};\n", Terminates: true);
    }
    // The jump statements end control flow — tagged Terminates so a switch case
    // section ending in one needs NO fall-through goto (see Visit(C.StmtSwitch)).
    public EmitContent Visit(C.StmtReturnVoid n) => new EmitContent.Text("return;\n", Terminates: true);
    public EmitContent Visit(C.StmtBreak n) => new EmitContent.Text("break;\n", Terminates: true);
    public EmitContent Visit(C.StmtContinue n) => new EmitContent.Text("continue;\n", Terminates: true);

    // switch (E) Block — switch body is a plain Block. `case X:` and
    // `default:` are statement-level labels (see CaseLabel/DefaultLabel)
    // that can appear anywhere inside the Block — including nested inside
    // a do-while or other control flow, enabling Duff's-device-shaped code.
    // C# accepts the same shape.
    public EmitContent Visit(C.StmtSwitch n)
    {
        // `switch (S1, …, C) Block` — comma controlling expr: lift the side effects
        // before the switch, switch on the last operand.
        if (CommaOpsOf(n.Arg2) is { Count: > 1 } ops)
        {
            var body = SwitchBody(n.Arg4);
            var result = $"{{\n{IndentEach(CommaLeadingStmts(ops))}"
                + $"switch ({StripOuterParens(ops[^1])}) {body}}}\n";
            if (_switchHoistedLabels is { Length: > 0 })
            {
                result += $"\ngoto __hoisted_{_switchHoistN};\n{_switchHoistedLabels}\n__hoisted_{_switchHoistN}: ;\n";
                _switchHoistedLabels = null;
                _switchHoistN++;
            }
            return result;
        }
        // A C `switch` is int-semantic (the controlling expression is integer-
        // promoted, case labels converted to that type), but dotcc lowers enums to
        // real C# enums — and C# rejects `switch(int) { case Enum.X: }` AND
        // `switch(Enum) { case (int)… }`. Decay an enum-typed subject to (int) so
        // it matches the (int)-decayed enumerator case labels below (uniform int =
        // pure C semantics). A non-enum subject is untouched.
        var switchBody = SwitchBody(n.Arg4);
        var switchResult = $"switch ({IntDecay(n.Arg2)}) {switchBody}";
        if (_switchHoistedLabels is { Length: > 0 })
        {
            switchResult += $"\ngoto __hoisted_{_switchHoistN};\n{_switchHoistedLabels}\n__hoisted_{_switchHoistN}: ;\n";
            _switchHoistedLabels = null;
            _switchHoistN++;
        }
        return switchResult;
    }

    // Emit a switch body, handling C fall-through. C lets a case section fall into
    // the next case when it doesn't end in break/return/…; C# forbids that
    // (CS0163), and forbids the final case falling out of the switch (CS8070). The
    // block's statement pieces are grouped into case sections (a `case`/`default`
    // label piece + the plain pieces up to the next label); a section whose LAST
    // piece doesn't terminate gets an explicit `goto case <nextLabel>;` /
    // `goto default;` (or a trailing `break;` for the final section) — exactly the
    // jump C's implicit fall-through performs. Stacked labels (`case A: case B:`,
    // nested in one piece) and sections that already terminate are left alone. A
    /// <summary>
    /// Like <see cref="string.Replace(string,string)"/> but skips occurrences
    /// inside nested <c>switch</c> blocks, where a goto-to-default rewrite
    /// would target the wrong switch (C# scopes <c>goto default;</c> to the
    /// innermost enclosing switch).
    /// </summary>
    private static string ReplaceOutsideNestedSwitches(string text, string from, string to)
    {
        // Quick check first: if there's nothing to replace or no nested switches, use fast path.
        if (!text.Contains(from, StringComparison.Ordinal) || !text.Contains("switch", StringComparison.Ordinal))
            return text.Replace(from, to);

        var result = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var nextSwitch = text.IndexOf("switch", i, StringComparison.Ordinal);
            var nextFrom = text.IndexOf(from, i, StringComparison.Ordinal);
            if (nextFrom < 0) { result.Append(text, i, text.Length - i); break; }
            if (nextSwitch < 0 || nextFrom < nextSwitch)
            {
                // Replace this occurrence: it's before any nested switch.
                result.Append(text, i, nextFrom - i);
                result.Append(to);
                i = nextFrom + from.Length;
            }
            else
            {
                // Append up to the nested switch; then skip the entire switch block.
                result.Append(text, i, nextSwitch - i);
                // Find the matching closing brace of the nested switch.
                var depth = 0;
                var j = nextSwitch;
                var inSwitch = false;
                while (j < text.Length)
                {
                    if (text[j] == '{') { depth++; inSwitch = true; }
                    else if (text[j] == '}') { depth--; if (depth == 0 && inSwitch) { j++; break; } }
                    j++;
                }
                result.Append(text, nextSwitch, j - nextSwitch);
                i = j;
            }
        }
        return result.ToString();
    }

    // body that isn't a piece-carrying block (an empty `{ }`) emits verbatim.
    private string SwitchBody(Item blockItem)
    {
        if (blockItem.Content is not EmitContent.Text { BlockPieces: { } pieces } || pieces.Count == 0)
        {
            return T(blockItem);
        }
        var labels = new List<int>();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].CaseLabelExpr is not null || pieces[i].IsDefaultLabel) { labels.Add(i); }
        }
        if (labels.Count == 0) { return T(blockItem); }
        var texts = pieces.Select(p => p.Text).ToList();
        for (var k = 0; k < labels.Count; k++)
        {
            // Section k spans [labels[k] .. next label); its last piece is the one
            // C would fall out of.
            var sectionEnd = k + 1 < labels.Count ? labels[k + 1] : pieces.Count;
            var last = sectionEnd - 1;
            if (pieces[last].Terminates) { continue; }   // already ends control flow
            string jump;
            if (k + 1 < labels.Count)
            {
                var next = pieces[labels[k + 1]];
                jump = next.IsDefaultLabel ? "goto default;\n" : $"goto case {next.CaseLabelExpr};\n";
            }
            else { jump = "break;\n"; }   // final section: C falls out, C# needs break
            texts[last] += jump;
        }
        // Build label→case-expression map for labels that start a case section.
        // In C, `goto label;` can jump into another case's block from a different
        // case; C# forbids cross-section `goto` (CS0159) and requires `goto case X;`
        // instead. The Lua VM dispatch loop relies on this heavily (l_tforloop,
        // l_tforcall). A label that isn't at a case start (e.g. a shared `ret`
        // handler) isn't mapped here — those need the label hoisted above the switch.
        var labelCaseMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var piece in pieces)
        {
            if (!((piece.CaseLabelExpr is not null || piece.IsDefaultLabel) && piece.Text.Length > 0))
                continue;
            var casePrefix = piece.IsDefaultLabel ? "default:\n" : $"case {piece.CaseLabelExpr}:\n";
            if (!piece.Text.StartsWith(casePrefix, StringComparison.Ordinal)) continue;
            var afterCase = piece.Text[casePrefix.Length..];
            var m = Regex.Match(afterCase, @"^\s*\{?\s*(@?[A-Za-z_]\w*):\n");
            if (!m.Success) continue;
            var labelName = m.Groups[1].Value;  // includes @ if it's a C# keyword
            var caseExpr = piece.IsDefaultLabel ? "default" : piece.CaseLabelExpr!;
            labelCaseMap[labelName] = caseExpr;
        }

        // Rewrite `goto label;` → `goto case expr;` for labels mapped above.
        // The replacement skips text inside nested switch blocks: in C#, `goto
        // default;` inside a nested switch jumps to the *inner* switch's default
        // case, not the outer label — so the inner goto must keep its named-label
        // form. (Lua's lstrlib.c triggers this with `goto dflt;` inside an inner
        // switch that targets a `dflt:` label on the outer `default:` case.)
        if (labelCaseMap.Count > 0)
        {
            for (var i = 0; i < texts.Count; i++)
            {
                foreach (var (label, caseExpr) in labelCaseMap)
                {
                    var from = $"goto {label};\n";
                    var to = caseExpr == "default" ? "goto default;\n" : $"goto case {caseExpr};\n";
                    texts[i] = ReplaceOutsideNestedSwitches(texts[i], from, to);
                }
            }
        }

        // Hoist shared labels that are NOT at case starts but are targeted by
        // cross-CASE gotos. These labels live inside a case's brace block, so
        // `goto label;` from another case section can't reach them (CS0159).
        // Extract each such label + body, place it OUTSIDE the switch.
        _switchHoistedLabels = null;
        // Map each piece index to its case section index
        var pieceSection = new int[texts.Count];
        for (var k = 0; k < labels.Count; k++)
        {
            var secStart = labels[k];
            var secEnd = k + 1 < labels.Count ? labels[k + 1] : texts.Count;
            for (var p = secStart; p < secEnd; p++)
                pieceSection[p] = k;
        }
        for (var i = 0; i < texts.Count; i++)
        {
            // Find labels in this piece that aren't case-start labels
            var defRe = new Regex(@"\n\s*(@?[A-Za-z_]\w*):\n");
            foreach (Match dm in defRe.Matches(texts[i]))
            {
                var defLabel = dm.Groups[1].Value;
                if (labelCaseMap.ContainsKey(defLabel)) continue;
                // Only hoist known cross-case labels. General hoisting needs a
                // more precise extraction that doesn't corrupt brace structure.
                // For now, `ret` is the only label needing this in the Lua VM.
                if (defLabel != "ret") continue;
                // Only hoist if a goto from a DIFFERENT CASE SECTION targets this label
                var crossSection = false;
                for (var j = 0; j < texts.Count; j++)
                {
                    if (pieceSection[j] != pieceSection[i]
                        && texts[j].Contains($"goto {defLabel};\n"))
                        { crossSection = true; break; }
                }
                if (!crossSection) continue;
                // Find the label definition's position
                var findRe = new Regex($@"\n\s*{Regex.Escape(defLabel)}:\n");
                var fm = findRe.Match(texts[i]);
                if (!fm.Success) continue;
                var defIdx = fm.Index + 1; // skip \n
                var tail = texts[i][defIdx..];
                // Extract from label to the case-closing `}`. The label body is
                // everything from the label def to the last `}\n` in the piece
                // (which closes the case). Keep the closing brace in the case.
                var lastClose = tail.LastIndexOf("\n}", StringComparison.Ordinal);
                if (lastClose < 0) continue;
                var hoistBody = tail[..lastClose];    // label + body
                var keepInCase = tail[lastClose..];    // closing brace + suffix
                if (hoistBody.StartsWith('\n')) hoistBody = hoistBody[1..];
                // Remove label body from case, add goto to reach the hoisted label.
                // The case body before the label originally fell through to it;
                // after hoisting we need an explicit goto. This also correctly
                // terminates the case (no fall-through to the next C# case section).
                texts[i] = texts[i][..defIdx] + "goto " + defLabel + ";\n" + keepInCase;
                _switchHoistedLabels = (_switchHoistedLabels ?? "") + hoistBody;
                break; // one label per piece
            }
        }

        return "{\n" + IndentEach(string.Concat(texts)) + "}\n";
    }

    // Hoisted labels extracted from SwitchBody — placed after the switch by
    // Visit(C.StmtSwitch) so they're reachable by `goto` from within any case.
    private string? _switchHoistedLabels;
    private int _switchHoistN;  // unique suffix for hoisted-label barriers

    // Statement-level case/default labels. Body is a single Stmt (which
    // may itself be another labeled stmt — `case 1: case 2: do_thing();`
    // chains naturally). An enumerator case label (`case TK_NAME:`) decays to its
    // (int) constant value so it matches the int-decayed switch subject; a label
    // that's already an int constant expression is untouched (an enum operand of a
    // `case A | B:` expression already decayed at the operator).
    public EmitContent Visit(C.CaseLabel n)
    {
        var label = IntDecay(n.Arg1);
        // Carry the label expr (for the matching `goto case <label>;`) and whether
        // the labeled statement terminates (so the fall-through pass knows if this
        // is the section's terminating piece).
        return new EmitContent.Text($"case {label}:\n{T(n.Arg3)}",
            CaseLabelExpr: label, Terminates: TerminatesOf(n.Arg3));
    }
    public EmitContent Visit(C.DefaultLabel n) =>
        new EmitContent.Text($"default:\n{T(n.Arg2)}",
            IsDefaultLabel: true, Terminates: TerminatesOf(n.Arg2));
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
    public EmitContent Visit(C.StmtGoto n) => new EmitContent.Text($"goto {Id(T(n.Arg1))};\n", Terminates: true);

    // `label: Stmt` — emit the label followed by the body statement.
    // Whitespace shape: label on its own line for readability.
    public EmitContent Visit(C.StmtLabel n) =>
        new EmitContent.Text($"{Id(T(n.Arg0))}:\n{T(n.Arg2)}", Terminates: TerminatesOf(n.Arg2));

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
        // A void-typed ternary used as a statement (Lua's GC write-barrier
        // macros `(cond ? luaC_barrier_(…) : cast_void(0));`) → its if/else form.
        // C# can't express the `?:` itself (a void call/cast isn't a valid arm).
        if (n.Arg0.Content is EmitContent.VoidCond vc)
        {
            return $"{{\n{IndentEach(vc.IfStatement)}}}\n";
        }
        // A value-context comma needing hoisting reaches statement position (Lua's
        // `f->code = luaM_newvectorchecked(…);`): emit the leading statements, then
        // the value expression as a discard statement, all in one block.
        if (n.Arg0.Content is EmitContent.SeqExpr se)
        {
            var body = string.Concat(se.LeadingStmts) + $"{StripOuterParens(se.ValueExpr)};\n";
            return $"{{\n{IndentEach(body)}}}\n";
        }
        if (CommaOpsOf(n.Arg0) is { } ops)
        {
            var sb = new StringBuilder();
            foreach (var op in ops) { sb.Append(StripOuterParens(op)).Append(";\n"); }
            // Wrap a MULTI-statement comma in a block so the whole comma stays ONE
            // C# statement: a braceless enclosing `if`/`while`/`for` body would
            // otherwise take only the first statement and the rest (plus any `else`)
            // would detach. Lua's `luaL_addchar(B,c)` = `((void)(…), (…))` as a
            // braceless `if`/`else` body is the motivating case.
            return ops.Count > 1 ? $"{{\n{IndentEach(sb.ToString())}}}\n" : sb.ToString();
        }
        // CS0201: bare parenthesized assignment isn't a statement. Peel the
        // outer parens that our binop emitters wrap on.
        return $"{StripOuterParens(T(n.Arg0))};\n";
    }

}
