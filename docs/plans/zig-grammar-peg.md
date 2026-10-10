# zig-grammar-peg: control flow as expressions, the way zig's grammar has it

**Status:** P0 done (this plan and the spike behind it, 2026-10-10). **P1 done**: P1a (the open/closed cascade and the
`if` expression as an open primary; probe 422 to 433) and P1b (`return` / `break` / `continue` as open primaries; probe
433 to 438). P2 started: the `else |err|` statement form without a then-capture (probe 438 to 443). P3a done:
the else-less `if` expression and block arms (probe 443 to 452). P3 first step done: `switch` as a closed
primary (probe 466 to 473); labeled blocks and value loops as primaries remain. P5 started: an inline
`struct { … }` is a Type anywhere (probe 473 to 486). Range, multi-object and capture-`while` value loops
(still RhsExpr forms, not primaries) took the probe to 491.

## Why

`zig.lalr.yaml` keeps control flow out of the operator cascade. `if`, `switch`, labeled blocks and loops are
`RhsExpr` alternatives, never a `Primary`, and `return` / `break` / `continue` are statements. That made the
grammar conflict-free early on, but every context zig allows them in needs its own copy:

- the arm nonterminals `ReturnArm`, `AssignArm`, `JumpArm`, `FallbackArm`, `CaptureArm`, `TypeArm`, `ConcatIf`,
  `IfOperand`, `ProngJump`;
- a value `if` with a `return` / jump arm in each position (`ifExprReturnThen`, `ifExprElseJump`, `fbIfJumps`, …);
- operator-specific patches: `boolOrSwitch`, `boolAndSwitch`, `addSwitch`, `switchCmp*`, `cmpEqIf`, `concatIf`, and
  the `orelse if` / `catch if` RhsExpr rules;
- statement `if` forms per arm shape (`stmtIfReturnElse`, `stmtIfAssignElse`, `stmtIfCaptureReturnErrElse`, …).

After the 2026-10-09/10 worklist batches (probe 341 to 422 of 553), what is left at the head of the std parse
probe is almost entirely more of these: an else-less `if` as a prong body, a braceless `if` body before
`else if`, an `if` whose body is an `if`, `else |_|` on a statement `if`. Each would be one more copy.

zig's PEG has none of this: `IfExpr`, `return Expr?`, `break :l Expr?`, `continue`, `comptime Expr`, `Block`,
labeled loops and `CurlySuffixExpr` are all `PrimaryExpr`, so they are operands anywhere, and an open-ended one
(`if … else E`, `return E`) takes everything to its right because PEG choice is greedy.

## The LALR(1) design: an open/closed cascade

A greedy trailing expression does not come free in LALR(1). The 2026-10-10 spike measured it.

- **`Primary -> 'if' (Expr) Expr 'else' Expr` naively:** 15 unresolved conflicts, plus 231 decisions the table
  builder settles silently by group precedence. Some of those are wrong. LALR.CC ranks the cascade's groups
  loosest-first, so inside an `if` arm `a + b % c` would reduce as `(a + b) % c`. That is the hazard in the
  "grammar precedence settles conflicts silently" memory, at scale.
- **An open/closed split:** every cascade level `L` (BoolOr, BoolAnd, Compare, Bitwise, BitShift, Add, Mul,
  Prefix) gets an open twin `LO`, whose rightmost operand is open: `AddO -> Add '+' MulO | MulO`. Open
  primaries (`if … else E`, later `return E`, `break :l E`) are reached only through `PrefixO`, so an open
  expression is always the rightmost operand. Nothing can follow it at an outer level, and no state has to
  choose between extending the arm and closing it. Result: **0 conflicts, and 1 new precedence decision**.
  That one was the old `IfExpr` arm forms competing for the same prefix, which P1 removes. The twins reuse
  their closed twin's action, so the AST records and their lowering stay as they are: 47 rules, 9 symbols,
  1556 states.

Closed primaries that end in `}` (`switch`, a labeled block, a `{}` block, a container type) need no open
twin; they become ordinary `Primary` alternatives.

**Statement position** is the other half. zig's `Statement` tries `IfStatement` / `SwitchExpr` / loops before
`AssignExpr ;`, so a statement-start `switch (x) {…}` is a statement even if `*p = 1;` follows. Here the
statement forms stay their own productions: at statement start, LALR's states for the statement form and the
expression form have different item cores, so their choice is local to that state and never merged into a
value context. Where zig's statement accepts an expression body (`if (c) f() else g();`), the
expression-statement path parses it as an `if` expression for free.

## Verification for every phase

1. No `GrammarConflictException`, and the `LALRCC_RESOLVED_DUMP` diff against `main` reviewed line by line. The
   aim is no new precedence decisions at all; each one that remains is explained in the PR.
2. Parse-shape pins for operator precedence inside and around the new forms (`a + b % c` in an arm,
   `if (c) a else b + d` taking the `+ d`, `x orelse if …`), so a silent misparse fails a unit test.
3. Unit suite, then the functional suite with the zig oracle (build `DotCC.FunctionalTests` first), and an
   oracle row per phase checked by hand against zig.
4. The std parse probe never drops (422 at P0).
5. Lowering: a phase replaces records, so each removed record's lowering moves to the new one at the positions
   it supported. A position it did not support stays a loud `IrUnsupportedException`.

## Phases

- **P1, value `if` and the jumps as open primaries.**
  - The open/closed cascade.
  - `OpenPrimary -> if (Expr) Payload? Expr else Payload? Expr`, with an arm that may also be a container type
    (today's `TypeArm`).
  - `return Expr?`, `break [:l] [Expr]`, `continue [:l] [Expr]` as open primaries.
  - Removes `IfExpr`'s arm variants, `ConcatIf`, `IfOperand`, `JumpArm`, `ReturnArm`, the `orelse`/`catch`
    `return` and `FallbackArm` jump forms, `fbReturnIf`, and the `RhsExpr` `orelse if` rules.
  - Lowering: a jump in value position is noreturn. It lowers where it does today (an `orelse`/`catch`
    fallback, an `if` arm in an initializer or statement) and is rejected elsewhere.
- **P2, statement `if` as zig's `IfStatement`:** `if (c) |p| Block (else |e| Statement)?` and
  `if (c) |p| AssignExpr (';' | else |e| Statement)`, with the capture forms folded through one optional
  `Payload`. Removes the per-arm statement forms (`stmtIfReturnElse`, `stmtIfAssignElse`, `AssignArm`, …).
  Should take the remaining `if` buckets in the probe head.
- **P3, closed primaries:** `switch`, labeled blocks, `{}` and value loops become `Primary` alternatives
  (`while … else E` is open). Removes `boolOrSwitch`, `addSwitch`, `switchCmp*`, `comptimeSwitchExpr`, most of
  `RhsExpr` and `FallbackArm`, and the `FieldValue` copies.
- **P4, payloads:** one optional `Payload` (`|x|`, `|*x|`, `|x, i|`) on `if`, `while`, `for`, `catch`, `else`,
  folding the per-capture record twins.
- **P5 (optional), container types as expressions:** `struct {…}` / `enum {…}` / `union {…}` as primaries,
  with `const X = struct {…};` an ordinary `VarDecl`. This is the largest lowering change, since every
  `*Decl` container record is read in many places. Do it only if P1 to P4 leave container copies that keep
  costing.

Each phase is one PR (P1 may be two: cascade plus `if`, then the jumps), merged before the next starts.

**P1a as landed.** 49 rules, 10 symbols (#198 to #207: the eight open twins, `OpenPrimary`, `IfArm`). Every
`if` arm is an `IfArm` (a value or a container `TypeArm`), so the type-arm record variants folded into `ifExpr` /
`ifExprCapture` with their slots unchanged; `IfOperand`, `ConcatIf`, `cmpEqIf`/`cmpNeIf`, `comptimeIfExpr` and the
`orelse`/`catch` + IfExpr RhsExpr and FieldValue rules are gone (their cases became ordinary nodes with an `IfExpr`
operand: `concat` over an `if` keeps its distributing lowering, `comptime if` is `preComptime`). No conflicts; 11 new
precedence decisions, each read: ten at statement start, where the statement form (a block, `return` arm, loop,
labeled block or `switch` as an `if` body before `else`) wins as before, and one shifting `:` after an identifier
into a labeled block at the end of an `if` arm, which is zig's reading (only `s[a.. if (c) b else n :0]`, an `if`
ending in a bare name as a sentinel slice's end bound, parses differently). Parse-shape pins in `ZigGrammarTests`,
oracle row `if_expression_as_operand`.

**P1b as landed.** `JumpExpr` (#208): `return RhsExpr?` and the six `break` / `continue` spellings, in a final
`rightmost` group (a `:` shifts into a label; a value-less jump's reduce ranks lowest, so a following value shifts, the
GH #283 lesson). The break / continue records are the fallback arms' `fb*`, so `LowerFallbackArm` kept its cases; a
control-flow fallback is now the ordinary `orElse` / `catchOp` / `catchCapture` with a noreturn right operand
(`IsNoreturnArm`), and a value `if` with a jump arm is the ordinary `ifExpr`. Retired: `orElseReturn` / `catchReturn`
(and the void forms), `JumpArm`, the FallbackArm and CaptureArm jump and `return` forms, `fbIf*`, the four `ifExpr`
jump-arm variants, and `ReturnArm`. Keeping `ReturnArm` beside `JumpExpr` was tried first and failed: LALR merged
the state after `return Expr` between a value loop's `else return v` (followed by `;`) and a statement `if`'s then-arm,
so `if (c) return x;` reduced to ReturnArm and demanded `else` (127 unit failures). The statement `if` and prong forms
take `JumpExpr` directly, and the value loops' `else return` forms fold into their `else RhsExpr` twins. Every new
precedence decision was read: statement / prong forms keep today's parse where they share a prefix with the expression,
the label `:` shifts, and a value shifts after a value-less jump. A jump as a plain operand (`f(return 1)`) parses and is
rejected by name in lowering. Pins in `ZigGrammarTests` (including the `return a == b & c`, `f(a orelse continue)` and
`f() catch return` regressions the old copies were shaped around); oracle row `jumps_as_expressions`.

**P2, first step.** `if (eu) Stmt else |err| Stmt` (std's Io/Dir.zig): an error-union `if` statement whose success
value is discarded (`stmtIfElseErr`, lowered as `LowerIfCapture` with the `_` capture). `if (c) f() else g();` already
works since P1a (an `if` expression statement with call arms). The captures stay inline tokens here: a `Payload`
nonterminal in the statement `if` alone would conflict with the expression `if` and the prong forms that share its
prefix, so it goes into all three at once in P4. **What is left at the probe head needs P3**: a `{ … }` block as an `if`
arm (`.linux => if (c) { … } else { … },` in Thread.zig, `return if (opt) |v| { … }` in Build.zig) and an else-less
`if` as an expression (`if (opt) |p| if (n > 0) { … };` in Io/Writer.zig, `=> |lib| if (c) return true,` in
Build/Step/Compile.zig). In zig a block is a PrimaryExpr and `if`'s `else` is optional; both come with P3's closed
primaries.

**P3a as landed.** zig's IfExpr has an optional `else`, and a block is a PrimaryExpr. `ifExprNoElse` /
`ifExprCaptureNoElse` (in a final rightmost group, so an `else` shifts into the full form: the dangling else binds to
the nearest `if`), and an `IfArm` may be a non-empty `{ … }` block (the statement Block's record; an empty `{}` stays
the void value). An `if` whose arms are statements (`IsStatementIf`) has a meaning only where a statement stands, so
`LowerStatementIf` takes it from an expression statement, a prong body (`LowerProngExprStmt`) and a tagged-union
capture prong (`ProngValue`): a comptime condition keeps only the taken arm, otherwise a runtime `if`. The four
else-less prong records (`prongIfExpr`, `prongIfCapture`, `prongIfSwitch`, `prongIfBlock`) retired into it. One more
piece: `if (opt) |p| if (n > 0) { … };` takes the statement form for the `if` and its block, so the `;` is an empty
statement (`emptyStmt`; zig allows the `;` only there, accepting it anywhere changes no meaning). The new precedence
decisions are statement forms winning where they share a prefix (assignment operators, `Stmt -> Expr ;`, the Block
over the block arm) and the `else` shift. Oracle row `statement_if_expressions`.

**P3, first step as landed.** `Primary -> SwitchExpr`: a `switch` ends in `}`, so it is a CLOSED primary, an operand
anywhere (`3 * switch … + 1`, `a or switch …`, `switch … < 0`, a tuple element, a field or fn-return type). Retired:
`boolOrSwitch`, `boolAndSwitch`, `addSwitch`, the six `switchCmp*`, `comptimeSwitchExpr` (now `preComptime` over a
SwitchExpr), `fbSwitch` (a `switch` fallback is the ordinary `orElse` / `catchOp` / `catchCapture`, which
IsControlFlowFallback still treats as a statement-shaped fallback), `structFieldSwitch` and `tyFnSwitchRet` (a
type position already derives a Primary, so they had become ambiguous; the fn-type cut is gone with it), and the
RhsExpr / Arg / FieldValue / `if (switch …)` copies. The new precedence decisions are the statement `switch` winning
at statement start on tokens that begin the next statement (`*p = 1;` after a `switch` statement). Two things tried
and kept out: a labeled block as a primary (`IDENT ':'` as an expression start met field declarations, and a container
field `name: switch …` reduced as a labeled switch), and value loops as open primaries (a value `inline for` met the
inline prong's case values, a real reduce/reduce conflict). Both need a narrower form.

**P5, first step as landed.** `Type -> InlineStructTy`: an inline `struct { … }` is a Type like any other, so it
nests (`[N]struct {…}`, `*struct {…}`, `?*const struct {…}`), types a literal (`[_]struct {…}{…}`, std's
compress/flate/token.zig) and is an argument, prong value or `if` arm without a copy of its own. Retired: the AType,
Arg, ProngType and TypeArm struct rules and the InlineStructTy CurlySuffix (all reached through Type now;
`typeArmStruct` lowering reads InlineStructType). An `if`-typed field takes a default and shares the plain
`structField` / `structFieldDefault` records (`structFieldIf` retired; LowerType folds the `if`). The only new
precedence decisions are `const X = struct {…};` and `return struct {…};` keeping their declaration forms on `;`.
The named declarations stay as they are; `enum` / `union` and the layout forms are the next candidates. Oracle row
`inline_struct_types` also caught a lowering bug: a struct-array field initialized from a tuple copied 0 bytes,
because a struct element's size is the C# compiler's to settle (`count * sizeof(T)` now).

**Value loops, widened (not yet primaries).** The value `for` over a range or several objects and the capture `while`
are LoopExpr forms (`forRangeElseExpr`, `forMultiElseExpr`, `whileCaptureElseExpr`) whose `else` is an IfArm, so a jump
or a `{ … }` block stands there. Lowering builds the statement loop under the loop-value target, and an `else` that
never completes runs as a statement. The new precedence decisions are only the statement loop keeping `{…} else` at
statement start, the same as the older value loops. Making loops primaries is still blocked on the `inline for`
reduce/reduce conflict with inline-prong case values.
