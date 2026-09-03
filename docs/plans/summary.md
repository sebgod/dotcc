# Plans — status index

One row per campaign plan in this directory: where it actually stands, and what is left.
`/plan-status` renders this as a sorted table; **this file is the thing that has to be
kept true** (the skill only reformats — it never re-checks a claim against the code).
Update a row in the same commit that changes a plan's real state.

Not plans, so not rows here: [`deferred.md`](deferred.md) (the cross-cutting ledger of
deliberate cuts still on the books — parse-only lowering gaps, deferred grammar,
runtime-fidelity divergences) and the two committed probe reports
([`std-parse-probe.report.txt`](std-parse-probe.report.txt),
[`wasm-surface-probe.report.txt`](wasm-surface-probe.report.txt)), which are regenerated
data rather than plans. Permanent out-of-scope lives in the SUPPORT docs.

Last verified against `main` on **2026-09-03** (`89f36e2`, plus S4d on `feat/zig-type-position-navigation`).

| Plan | Status |
|---|---|
| [fable-wasm](fable-wasm.md) | **NOT STARTED ~10%** — binary-`.wasm` front-end (`.wasm` → IR → C#), end-goal consuming Embedded Swift. WF0 (the surface probe) is ✅ done and its findings are committed; nothing is built. **Remaining:** WF1 the strict binary reader (next, self-contained), then WF2 the lifter, WF3–WF8. |
| [road-to-zig-std](road-to-zig-std.md) | **PARTIAL ~35%** — compiling real upstream zig std from source. ✅ S0 probe, S1 module graph, S2 lazy decl-driven lowering, S4a/b/c comptime-value + captured-`if` folding, S4d type-position module navigation, several S9 parse bricks (probe at 32.0% of 553 files), G1 (`std.ascii` from real source, oracle-identical), and G4's tentpole (a reified generic carries methods). **Remaining:** S3 synthetic `builtin`/`root`, S5–S7 the `@typeInfo` reflection engine, S8 the platform-floor redirect table, the ranked S9 bricks, G2/G3/G5, and G4's blockers 2/3/5. |
| [fable-web](fable-web.md) | **DONE ~95%** — the Blazor-WASM in-browser sandbox, live at sebgod.github.io/dotcc (landing + sandbox with the C/Zig toggle + coverage + story pages), AOT-published by CI. WEB0–4, WEB6, WEB7 all ✅. **Remaining:** WEB5 stretch flexes only, none scheduled — runtime-less NativeAOT-LLVM `dotcc.wasm` (must be an x64 CI spike), offline PWA, a compiler-explorer diff view, direct binary `.wasm` emit. See [`fable-web.findings.md`](fable-web.findings.md). |
| [fable-wall](fable-wall.md) | **DONE** — the generative Zig core (comptime types / generics / `anytype`), W0–W6, the whole planned arc merged; monomorphization over retained ASTs, no Sema. Non-arc follow-on work moved to road-to-zig-std. |
| [fable-c](fable-c.md) | **DONE** — the C follow-up batch (`unreachable()`, `int_least`/`fast`, `[[nodiscard]]`, `-Wimplicit-fallthrough`) all shipped; the plan is exhausted. C89/C99 complete and the C11/C23 roadmap column is empty — what is left is nice-to-have (`_BitInt(N)`, decimal FP, the multibyte↔wide conversions) or deliberately out of scope. |
| [fable-zig](fable-zig.md) | **DONE** — the Zig quick-wins inventory; every implementable item shipped and the formal milestone roadmap (I→T, ß, U/V/W/X/Y/Z) is exhausted. Its "the wall" section was relitigated by fable-wall and is now history. |
| [import-mode](import-mode.md) | **DONE** — clang-shaped `-l`/`-L` native import mode: dynamic GOT-style fn-ptr binding over `NativeLibrary`, static `.a`/`.lib` via `DllImport` + `DirectPInvoke` (NativeAOT publish only). Its honest limits (calls-only, no variadic/extern-data imports, no link-order interposition) are documented in the plan, not deferred work. |
