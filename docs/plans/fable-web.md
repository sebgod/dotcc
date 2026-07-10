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
  loads `DotCC.Lib` as ordinary managed .NET in the browser — nearly **zero
  compiler changes**: WEB0 found one (the generated LALR parse table's oversized
  `.cctor` broke the wasm *interpreter*) and fixed it once, in the LALR.CC
  generator (flat RVA-backed tables — see WEB0), so it runs on the lightweight
  **interpreted** path with **no AOT/Emscripten**. NativeAOT-LLVM (dotcc as one
  standalone `dotcc.wasm`) is smaller and faster but the .NET→browser LLVM path is
  still experimental — it is the named **v2 flex** (and the self-eating showcase
  feeds the wasm-frontend campaign's WF8), not the v1 plan. No server also means:
  no compile API to abuse, nothing to operate, nothing to fall over.
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

### WEB0 — the feasibility spike (S) — *measure before building* — ✅ DONE (2026-07-10)

A throwaway-quality Blazor page (`DotCC.Web.Spike`, referencing `DotCC.Lib`)
driven by a headless-Edge/CDP harness. **All five questions answered — GO.**

**The one real wall (found and cleared).** `DotCC.Lib` loads on the wasm runtime,
but the *first* `EmitWat` blew up: `InvalidProgramException: … 'DotCC.C:.cctor':
locals size too big`. Root cause: the LALR generator emitted the parse table as a
single 2-D `Action[,]` element-initialiser — for the C grammar **858 × 193 ≈
165 000 inline `new Action(...)`**, one temporary local per cell — which overflows
the Mono/WASM **interpreter's** 16-bit per-method frame. NativeAOT and the desktop
CLR compile it fine; only the browser interpreter chokes. **Fix (landed in
LALR.CC):** `TablesEmitter` now emits the table as two flat constant primitive
arrays (`byte[] _actionTypes` + `int[] _actionParams`) that Roslyn lowers to a
single `RuntimeHelpers.InitializeArray` over a `.data` RVA blob, rebuilt into the
`Action[,]` by a tiny loop → trivial `.cctor`, runs on the interpreter. This
**removes any need for AOT** (the campaign runs on the lightweight interpreted
Blazor path). A gotcha banked: the backing arrays must be declared *before* the
`ParseTable` field — C# runs static field initialisers in declaration order.
Ships as a LALR.CC release + dotcc NuGet cutover.

**Measurements (interpreted path, this box, 2026-07-10):**
- **(a) loads:** ✅ `DotCC.Lib` initializes under Blazor WASM.
- **(b) `EmitWat` over MEMFS:** ✅ **works unchanged** — wrote `/src/main.c` +
  `/src/util.h` to Emscripten MEMFS, `Compiler.EmitWat(["/src/main.c"])` produced
  6.8 KB of wat with the `#include "util.h"` **resolved by the stock
  FileSystemEnumerable include-scan**. **D5 = MEMFS; no string-source overload
  needed.**
- **(c) payload + latency:** **3.31 MB Brotli** over the wire (13.2 MB raw) — .NET
  runtime ~1.5 MB + `DotCC.Lib` 0.48 MB + ICU ~0.4 MB (trim with
  `InvariantGlobalization`, a C compiler needs no culture data). Compile latency
  **~3.4 s per `EmitWat`** (cold 3.8 s), the interpreter tax — usable for a
  click-Run-wait-a-beat sandbox (show a spinner); AOT or hot-path optimisation is
  a later lever, not a blocker.
- **(d) Zig through `EmitWat`:** ✅ a `.zig` sample lowered to wat (274 chars).
  **D7 = the Zig toggle is viable** (at least for wat-backend-covered surface;
  `std.debug.print`-class gaps remain wat-backend worklist, as planned).
- **(e) full JS pipeline:** ✅ dotcc's native `.wat` → vendored `libwabt.js`
  `parseWat().toBinary()` (716 B wasm) → `WebAssembly.instantiate` + the `fd_write`
  shim ported verbatim from `WatOracleTests` → `main()` ran, stdout exactly
  `web0=42\n`. Same V8 + wabt + shim the browser uses.

Cut: AOT was trialled as the no-code-change alternative but **abandoned** — the
flat-array fix makes it unnecessary, and its `mono-aot-cross` workers are a heavy
build. The `DotCC.Web.Spike` project + the node/CDP harness are archived in the
session scratchpad (not committed — WEB1 builds the real `DotCC.Web`).

### WEB1 — the run pipeline, ugly (M) — ✅ DONE (2026-07-10)

The end-to-end sandbox, functional (plain `<textarea>`, not yet CodeMirror). Landed:
`DotCC.Web/` Blazor WASM project (D6 — refs `DotCC.Lib` only, `IsPackable=false`,
out of the CPM chain + `dotcc.sln`, so untouched by CI; `InvariantGlobalization` +
`DotCC.Lib` as a `TrimmerRootAssembly` to keep the parse tables/embedded headers
whole); `libwabt.js` (wabt.js 1.0.36) vendored + pinned under `wwwroot/lib/wabt/`
with its provenance README (D3); `wwwroot/js/sandbox.js` — the run interop
(`parseWat().toBinary()` with the WF0 feature flags → `WebAssembly.instantiate` →
the `fd_write`/`proc_exit` shim capturing fd 1/2 → `main()` exit code); `Home.razor`
— source editor, Run, **Output / wat / C#** panes, diagnostics fed by
`CompileException` + `Console.Error` capture; source staged to MEMFS `/work/` (D5).
**Verified in headless Edge (CDP):** the default factorial program boots, compiles
(C→wat by dotcc-in-wasm), assembles + runs in-browser, and the output pane shows
`1! = 1 … 6! = 720`, exit 0. Cut to WEB2: CodeMirror, examples, Zig toggle,
multi-file, share-links. Deploy (GH Pages) is WEB4.

### WEB2 — the sandbox proper (M) — 🚧 IN PROGRESS

Landed in reviewable slices:

- **Slice A ✅ (2026-07-10)** — editor polish, no new deps: the **`-E` tokens tab**
  (4th output view, via `Compiler.Preprocess`), the **examples dropdown** (D8 —
  Factorial / FizzBuzz / Fibonacci / GCD / Primes, curated to the integer+`printf`
  surface the wat backend runs today), and **share-links** — source deflate-packed
  into a `#src=…` URL fragment via the native `CompressionStream` API (no
  lz-string/pako vendoring), copied to clipboard + reflected in the address bar,
  and re-hydrated on boot. Verified headless (CDP): factorial regression + FizzBuzz
  run, `-E` shows the token stream (incl. MEMFS-resolved `stdio.h`), share-link
  round-trips (UTF-8 safe).
- **Slice B ✅ (2026-07-10)** — **CodeMirror 6** (D4), the vendoring lift. CM6 ships
  as ES modules meant to be app-bundled (no official browser file), so we bundle our
  own with esbuild → one self-contained 410 KB IIFE and commit it (D3, no CDN),
  vendored under `wwwroot/lib/codemirror/` with `LICENSE` (MIT), a provenance README,
  and the `entry.mjs` + `package.json` build inputs for byte-reproducible regen. The
  bundle exposes `window.dotccEditor` (`create`/`getValue`/`setValue`); `Home.razor`
  mounts it, pulls text on Run/Share, pushes on example-select / share-load. Packages:
  `@codemirror/{view,state,commands,language,lang-cpp,theme-one-dark}` (C highlighting
  + oneDark). Verified headless (CDP): editor mounts, regression + FizzBuzz + `-E` +
  share still green.
- **Slice C ✅ (2026-07-10)** — **compiler-flag toggles**: a `-std=` dialect picker
  (`c90/c99/c11/c17/c23`, via `CDialect.Parse`) + `-Wconversion` / `-Wimplicit-fallthrough`
  / `-pedantic` checkboxes (via `WarningFlags`), read at Run time and threaded into
  `EmitWat` / `EmitCSharp` / `Preprocess`. Diagnostics are captured from BOTH emit
  passes and deduped (both run the shared dialect gate, so `-pedantic` lines would
  otherwise repeat; `-Wconversion` is a codegen-time gate only the C# pass runs).
  Verified headless (CDP): a narrowing `long`→`char` is silent with `-Wconversion` off
  and warns when on (`implicit conversion … may lose data … [-Wconversion]`).
- **Slice D (next)** — Zig toggle (D7) — needs an empirical Zig→wat measurement first
  (`std.debug.print` may exceed the wat backend's fd-1 surface; ship only examples that
  actually run) — and multi-file MEMFS tabs.

Full WEB2 scope:
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

### WEB4 — GitHub Pages deployment (S) — ✅ DONE + LIVE (2026-07-10)

**Live at <https://sebgod.github.io/dotcc/>.** First deploy green (build 3m16s +
deploy 11s); verified end-to-end against the LIVE URL in headless Edge (CDP): boots,
compiles C→wat in-browser, runs, and all WEB2 features (CodeMirror, examples, `-E`,
share-links, `-Wconversion` toggle) pass. Setup is documented in `DotCC.Web/README.md`.


`.github/workflows/pages.yml` (added): on push to main (touching `DotCC.Web`/
`DotCC.Lib`/`DotCC.Libc`) or manual dispatch — install `wasm-tools`, `dotnet publish`
`DotCC.Web` (Release, native-relinked runtime, Brotli), rewrite `<base href>` `/` →
`/dotcc/` in the published `index.html` only (local dev stays root), `touch .nojekyll`
(Jekyll would strip `_framework/`), `cp index.html 404.html` (SPA deep-link fallback),
`upload-pages-artifact` → `deploy-pages`. Pages enabled via the REST API
(`build_type=workflow`), so no manual Settings step. Target URL:
`https://sebgod.github.io/dotcc/`.

Deferred to a follow-up: the **CI smoke-check** (a node script driving the vendored
`libwabt.js` + fd_write shim over one committed `.wat`, proving the JS half in CI —
the Blazor half is already covered by the .NET suites; `EmitWat` doesn't change
behaviour by being browser-hosted). The run path is verified today via the headless
Edge/CDP harness (WEB1/WEB2).

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
