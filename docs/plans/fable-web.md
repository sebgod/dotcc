# The dotcc web sandbox — GitHub Pages + compile-and-run in the browser (2026-07-10, Fable)

> Campaign plan, git-tracked sibling of `fable-wasm.md` / `road-to-zig-std.md`.
> Independent arc: it consumes the compiler's public seams (`Compiler.EmitWat` /
> `EmitCSharp` / `Preprocess`) and the **wat backend**; no shared blocker with the
> wasm *frontend* campaign in either direction (that one consumes `.wasm`, this one
> produces it). Snapshot of main at `36ebf06` (WF0 merged).

**Goal.** A public GitHub Pages site for dotcc with two halves:

1. **The story** — what dotcc is (a clang-shaped C → .NET/C# transpiler with a Zig
   front-end and a wasm-text backend), what it can do (the C-SUPPORT / ZIG-SUPPORT
   coverage), and how it's built (the N×M frontends-meet-backends-at-one-typed-IR
   architecture).
2. **The sandbox** — an in-browser playground that **runs dotcc itself as wasm**:
   type C (or Zig), see the emitted C# / wat / preprocessor output live, and
   **execute the program in the browser** via the wat backend — C source →
   `EmitWat` → assemble → instantiate → stdout in an output pane. No server, no
   backend, no telemetry: a fully static site.

The pitch practically writes itself: *a C compiler, compiled to wasm, compiling C
to wasm, in your browser.* And it is honest advertising — the sandbox exercises
the real `DotCC.Lib`, not a mock.

## Why this is cheap (measured, not hoped)

The run pipeline is the existing `WatOracleTests` round-trip relocated client-side.
Every stage already exists and is proven over the 146-program corpus:

- **`Compiler.EmitWat` is a pure managed function** — `string` paths in, `.wat`
  text out. `DotCC.Lib` is `IsAotCompatible` with a runtime closure of exactly
  LALR.CC + the BCL (YamlDotNet is build-time, `PrivateAssets=all`). Nothing in it
  knows about an OS.
- **System headers need no filesystem.** `#include <stdio.h>` resolves from
  embedded assembly resources (`Compiler.Resources.cs` — `DotCC.SystemHeaders.*`
  manifest names). The include scan only touches disk for *user* headers next to
  the inputs — and under Blazor WebAssembly, `System.IO` runs against Emscripten's
  in-memory filesystem (MEMFS), so writing the editor buffers to `/src/main.c` and
  calling `EmitWat(new[]{"/src/main.c"})` should work **unchanged**. (WEB0 proves
  this; the fallback is a small string-source overload.)
- **wat → wasm in the browser**: wabt ships an official JS/wasm build
  (`libwabt.js`) exposing `parseWat(...).toBinary()` — the same wat2wasm the
  oracle leg already trusts, compiled to wasm. Vendored, pinned, Apache-2.0.
- **Instantiate + run + capture stdout**: `WatOracleTests.RunWatStdout`'s node shim
  is ~15 lines — provide `wasi_snapshot_preview1.fd_write`, read iovecs out of the
  exported memory, accumulate fd-1 bytes. `fd_write` is the **only** import the
  wat backend ever emits (re-confirmed by the WF0 probe report over all 146
  corpus modules). The shim ports to the browser verbatim.
- **Diagnostics are already strings.** Errors throw `CompileException` with a
  clang-shaped message; warnings print to `Console.Error`, which the sandbox
  captures via `Console.SetError` — the same redirection the fixture harness uses.

The other tabs are free: `EmitCSharp` and `Preprocess` return/write plain text —
render them with syntax highlighting and the sandbox doubles as a **look inside
the compiler** (source → tokens → C# → wat, stage by stage).

## Decisions (settled up front)

- **D1 — Blazor WebAssembly, not NativeAOT-LLVM, not a server.** GitHub Pages
  serves static files only, so the compiler must run client-side. Blazor WASM
  loads `DotCC.Lib` as ordinary managed .NET in the browser — **zero compiler
  changes**. NativeAOT-LLVM (dotcc as one standalone `dotcc.wasm`) is smaller and
  faster but the .NET→browser LLVM path is still experimental — it is the named
  **v2 flex** (and the self-eating showcase feeds the wasm-frontend campaign's
  WF8), not the v1 plan. No server also means: no compile API to abuse, nothing
  to operate, nothing to fall over.
- **D2 — The run path is the wat backend, and only the wat backend.** C# output
  is *displayed* (it's a string) but never executed in-browser — running it would
  mean shipping Roslyn to the browser (huge) and `unsafe` codegen on a runtime
  that can't JIT it. The wat backend IS dotcc's execution story on the web, and
  the sandbox is its showcase. Programs outside the wat backend's surface fail
  with the loud `CompileException` they already produce — surfaced in the
  diagnostics pane, not hidden (fail loudly, grow on purpose: every "unsupported"
  message a visitor sees is a real, measured gap — the probes' philosophy).
- **D3 — Fully self-contained static site: no CDN, no external requests.** All
  assets vendored and pinned: `libwabt.js` (committed blob + the version and
  regeneration command in a README, the sidecar-is-the-reference pattern), the
  editor bundle, fonts if any. A visitor's source code never leaves their tab —
  worth stating on the page, and it makes the site work offline once cached.
- **D4 — Editor: CodeMirror 6, vendored as one pinned esbuild bundle.** Monaco is
  heavier and CDN-shaped. A plain `<textarea>` is the WEB1 placeholder; CodeMirror
  arrives in WEB2 with C highlighting. The bundle is committed with its lockfile +
  one-command regeneration script (no npm on the always-on path).
- **D5 — In-browser filesystem: MEMFS first, string-overload only if forced.**
  WEB0 measures whether the resilient include scan (`FileSystemEnumerable` over
  Emscripten MEMFS) just works. If it does, the sandbox writes each editor tab
  (`main.c`, optional `util.h`, …) under `/src/` and calls the unchanged public
  API — multi-file support falls out of the existing `-I`/same-dir header
  resolution for free. Only if MEMFS misbehaves does `DotCC.Lib` grow a
  `(name, content)` pairs overload (small, clean, useful beyond the web — but
  don't add API on speculation).
- **D6 — Site layout: a new `DotCC.Web/` Blazor WASM project.** References
  `DotCC.Lib` only; `IsPackable=false`; **not** in the test chain and not part of
  `dotnet test`. The AOT-clean rule is untouched (`DotCC.Lib` gains no deps;
  Blazor is a consumer, like the CLI). Deployment is a separate
  `.github/workflows/pages.yml` (publish → `upload-pages-artifact` →
  `deploy-pages` on push to main), keeping `ci.yml` untouched — the chibi/lua
  workflow-separation precedent. `<base href>` set for project-pages
  (`/dotcc/`), `.nojekyll` so `_framework/` survives Jekyll.
- **D7 — Zig in the sandbox is empirical, not promised.** The IR and wat backend
  are frontend-agnostic, so `.zig` input *should* work via the same `EmitWat`
  seam — but the zig corpus has only ever run through the C# backend, and curated
  lowerings like `std.debug.print` → `fprintf(stderr, …)` may touch libc surface
  the wat backend hasn't grown (its corpus covers fd-1 `printf`/`puts`/`putchar`).
  WEB0 measures a zig sample through `EmitWat`; the language toggle ships if it
  holds, else it's a named cut with the gap on the wat backend's worklist (where
  it belongs — a compiler gap, not a website gap).
- **D8 — Examples come from the proven corpora, content from `docs/`.** The
  examples dropdown seeds from hand-picked `WatOracleTests` programs (known-good
  end-to-end by construction) plus `examples/`; the story pages adapt
  `C-SUPPORT.md` / `ZIG-SUPPORT.md` / `architecture.md` rather than inventing
  parallel prose that will rot. Engineering-heavy, marketing-lite.

## Architecture in one screen

```
┌───────────────────────────── browser (static site) ─────────────────────────────┐
│                                                                                  │
│  CodeMirror tabs        Blazor WASM (.NET runtime)          JS interop           │
│  main.c / util.h  ──►  MEMFS /src/*.c ──► Compiler.EmitWat ──► wat text          │
│                                     │            │                               │
│                                     │            ├─► Compiler.EmitCSharp ─► C# tab│
│                                     │            └─► Compiler.Preprocess ─► -E tab│
│                                     │                                            │
│                        CompileException / Console.SetError ──► diagnostics pane  │
│                                                                                  │
│  wat text ──► libwabt.js parseWat().toBinary() ──► WebAssembly.instantiate       │
│                     (vendored wat2wasm)              + fd_write shim (ported     │
│                                                        from WatOracleTests)      │
│                                                          │                       │
│                                              stdout bytes + main() return value  │
│                                                          ▼                       │
│                                                    output pane                   │
└──────────────────────────────────────────────────────────────────────────────────┘
```

## Milestones

Sizes: S < M < L. The WF0/S0 lesson holds: **the first milestone is a measuring
spike, and later feature lists are provisional until it lands.**

### WEB0 — the feasibility spike (S) — *measure before building*

A throwaway-quality Blazor page (not the real UI) that answers, with numbers
committed into this plan:
  (a) does `DotCC.Lib` load + run under Blazor WASM at all (LALR.CC parse-table
      init, embedded resources, `Console.SetError` capture);
  (b) does `EmitWat` over a MEMFS-written `/src/main.c` work **unchanged**,
      including a user header next to it (D5's decider);
  (c) payload size (Brotli, after trimming) and cold-start latency (first
      `EmitWat` call — the lazy parser-table cost) on a realistic connection;
  (d) does a `.zig` sample survive `EmitWat` (D7's decider);
  (e) does vendored `libwabt.js` assemble a dotcc-emitted module and does the
      ported `fd_write` shim run it (the full pipeline, once, end to end).
Deliverable: this section rewritten with the measurements; go/no-go on D5/D7.

### WEB1 — the run pipeline, ugly (M)

The end-to-end sandbox with a placeholder UI: one `<textarea>`, a Run button,
an output pane. `DotCC.Web/` project scaffolded properly (D6), `libwabt.js`
vendored + pinned with its README, the JS shim module (`wat2wasm` wrapper +
instantiate + `fd_write` capture + `main()` return code), Blazor↔JS interop
seam, diagnostics pane fed by `CompileException` + captured stderr. The
known-good corpus samples run; an unsupported program shows its loud error.

### WEB2 — the sandbox proper (M)

CodeMirror 6 (D4) with C highlighting; output tabs — **Run / C# / wat / -E
tokens** (the look-inside-the-compiler view); multi-file tabs backed by MEMFS
`/src/` (D5); the examples dropdown (D8); warning-flag toggles if cheap
(`-W...`, `-std=` dialect picker — they're just `EmitWat` parameters); the Zig
language toggle per D7's WEB0 verdict. Share-links: source LZ-compressed into
the URL fragment via the native `CompressionStream` API — shareable playground
links with no server and no dependency.

### WEB3 — the story pages (S/M)

Landing page (what dotcc is, the one-screen architecture, the pitch line) +
coverage pages adapted from `docs/C-SUPPORT.md` / `ZIG-SUPPORT.md` +
"how it works" from `architecture.md`. Every page links into the sandbox with a
pre-loaded example demonstrating the feature it describes (deep-link = a
share-link). Keep the docs the source of truth; the site adapts, never forks.

### WEB4 — GitHub Pages deployment (S)

`.github/workflows/pages.yml`: publish `DotCC.Web` (Release, trimmed, Brotli) →
`upload-pages-artifact` → `deploy-pages`, on push to main. `<base href="/dotcc/">`,
`.nojekyll`, SPA 404 fallback. A smoke-check step: after publish, a node script
drives the vendored `libwabt.js` + shim over one committed `.wat` to prove the
JS half of the pipeline in CI (the Blazor half is already covered by the .NET
suites — `EmitWat` doesn't change behavior by being hosted in a browser).

### WEB5 — flexes (stretch, unordered)

- **NativeAOT-LLVM `dotcc.wasm`** (D1's v2): one standalone module, no .NET
  runtime download — and the artifact the wasm-frontend campaign's WF8
  self-eating round-trip wants anyway.
- **Offline PWA manifest** (D3 already makes it work offline once cached).
- **A "compiler explorer" diff view** — two dialect/flag settings side by side.
- **Direct binary emit** — once the wasm-frontend campaign's encoder knowledge
  exists, emit `.wasm` bytes directly and drop `libwabt.js` from the pipeline
  (one less vendored dep; the wat *text* tab stays, it's the readable view).

## Validation story

- **The corpus is the oracle.** The sandbox's run path is `EmitWat` (covered by
  the .NET test suites) + the JS shim (covered by the WEB4 CI smoke-check over a
  committed module). The 146-program corpus already proves the pipeline's
  semantics under node; the browser differs only in the `WebAssembly` host
  object, which is the same engine (V8/JSC/SpiderMonkey) node uses.
- **No new always-on test infrastructure.** The site is a consumer of tested
  seams. A Playwright end-to-end leg is possible later but is NOT part of this
  campaign — the smoke-check + the existing suites bound the risk adequately for
  a demo site.
- **Payload/latency budgets** get measured in WEB0 and re-checked at WEB4 (the
  deploy workflow prints the published size; a regression is visible in the PR).

## Risks & empirical unknowns

- **Blazor+MEMFS friction** — the resilient include scan uses
  `FileSystemEnumerable`; if Emscripten's MEMFS trips it, D5's fallback overload
  is small and useful. WEB0 answers.
- **Payload size** — the Blazor .NET runtime + `DotCC.Lib` + generated parse
  tables, Brotli'd. Acceptable for a developer-audience demo; WEB0 puts a number
  on it, WEB5's NativeAOT path is the shrink story.
- **Cold-start** — LALR table construction on first compile. Likely fine
  (milliseconds-scale on native; browser ~2-5× slower); WEB0 measures, and a
  "warming up…" state is cheap UX if needed.
- **`libwabt.js` staleness** — pinned + vendored; only ever needs to track the
  wat features our backend emits (MVP + sign-ext + trunc_sat — WF0's histogram),
  which wabt has supported for years.
- **Zig-through-wat gaps** (D7) — a compiler-side worklist item if WEB0 finds
  one, not a site blocker.
- **GitHub Pages quirks** — base path, Jekyll underscore-stripping, 404 routing:
  all known, all in WEB4's checklist. No cross-origin-isolation headers needed
  (no threads, no SharedArrayBuffer).

## What this campaign is NOT

Not a server or a compile API (static files only). Not a general web IDE — no
accounts, no persistence beyond share-links. Not a package/deps story. Not the
wasm *frontend* campaign (`fable-wasm.md` consumes `.wasm`; this produces it —
they meet only at the shared wat-oracle corpus and, eventually, WF8/WEB5's
self-eating flex). Not a rewrite of the docs — the site adapts `docs/`, which
remains the source of truth. And not marketing over substance: every claim on
the page is backed by a runnable example in the sandbox.

## Progress-bar convention

WEB0's committed measurements (payload KB, cold-start ms, MEMFS verdict, zig
verdict) are the baseline, written into this plan. From WEB1 on, the visible
progress bar is the site itself: which corpus examples are in the dropdown and
run green in-browser. The deploy workflow's published-size line is the budget
regression check, PR over PR.
