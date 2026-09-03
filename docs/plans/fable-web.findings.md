# fable-web findings — notes from other repos' sessions

Companion to [`fable-web.md`](fable-web.md). Two sets of working notes made while working in
*other* repos on 2026-07-17 (`sharpastro/tianwen`, `sebgod/chess`) that bear on `DotCC.Web`.
They were kept uncommitted at the time; they are committed here verbatim (headings demoted one
level) so that they stop being the only copy, on one machine, outside version control.

**One verdict has since been overtaken by events:** the AOT note calls AOT "unnecessary" for
dotcc's compile-a-snippet sandbox — WEB7 (PR #100, the same day) turned `RunAOTCompilation` on
for the Pages publish regardless, measuring 4× Run latency for +1.7 MB brotli. The durable part
of that note is the **win-arm64 `mono-aot-cross` crash + its workarounds** (which WEB7 did not
need on ubuntu-x64 CI, but which anyone reproducing an AOT publish locally will hit) plus the re-confirmed
cuts (NativeAOT-LLVM, WASI, threads).

---

## WASM AOT findings relevant to DotCC.Web (from the tianwen web-showcase session, 2026-07-17)

Working notes (originally kept out of the repo — see the preamble). Source: `sharpastro/tianwen` branch `feat/web-showcase`
(`docs/plans/web-showcase.md`). These update the picture recorded in `docs/plans/fable-web.md`,
which trialled and cut AOT.

### What's new since fable-web.md's AOT cut

fable-web.md's reasons for cutting AOT were sound for dotcc's workload (the interpreter 16-bit
per-method frame fix made interpreted fast enough for parse/emit; mono-aot-cross workers heavy).
tianwen's data point complements rather than contradicts it:

- **AOT works, including locally on this win-arm64 box, with two workarounds** (below). tianwen
  measured **24x on catalog decode and 42x on a pure-numeric planner sweep** (38 s total -> 1.1 s),
  at +5 MB brotli payload (16 -> 21 MB). For compute-bound hot paths the win is transformative;
  for dotcc's compile-a-snippet sandbox it remains unnecessary.
- If the sandbox ever grows a genuinely compute-bound path (large translation units, the wasm
  frontend chewing big modules, batch coverage runs in-browser), `RunAOTCompilation=true` on the
  Pages publish is now a proven, low-risk lever - no code changes were needed in tianwen.

### The win-arm64 crash + workarounds (new information)

`mono-aot-cross` on win-arm64 fail-fasts with 0xC0000409 (`* Assertion at sgen-alloc.c:409,
condition '*p == NULL' not met`) on some inputs - tianwen hit it on P/Invoke-dense assemblies and
on the `WasmDedup` synthesized `aot-instances.dll`. Local workarounds that produced a clean AOT
publish:

```xml
<ItemGroup>
  <!-- identity must be the .dll file name (MSBuild %(Filename) batching against aot-in names) -->
  <_AOT_InternalForceInterpretAssemblies Include="Some.Assembly.dll" />
</ItemGroup>
```

plus `-p:WasmDedup=false`. Excluded assemblies stay interpreted (fine for cold/dead code).
ubuntu-x64 CI may need neither - try plain there first.

### Unchanged verdicts (re-confirmed this session)

- **NativeAOT-LLVM**: still experimental/x64-only/stale for browser wasm - fable-web.md's D1
  reasoning stands.
- **WASI**: server-side ABI, irrelevant to browser perf.
- **Threads**: browser wasm threads need COOP/COEP cross-origin isolation, which GitHub Pages
  cannot serve natively (coi-serviceworker hack aside); single-thread + AOT (or a Web Worker for
  long work) remains the practical model.
- The Pages deploy skeleton (Blazor WASM outside sln/CPM + own pages.yml) is now live in three
  repos' lineage: dotcc (WEB4), chess, tianwen (in flight).

---

## WebGl.Renderer 1.1: shared `<WebGlCanvas>` component (from the chess session, 2026-07-17)

Working notes (originally kept out of the repo — see the preamble). DotCC.Web doesn't render to a canvas today — this is a
pointer for if/when it wants GPU (or software) canvas rendering, so the wheel doesn't get
reinvented a third time.

`SharpAstro/WebGl.Renderer` 1.1 (NuGet) now ships two reusable pieces for Blazor WASM apps:

1. **`WebGlRenderer`** — WebGL2 backend for DIR.Lib's `Renderer<TSurface>`: command-buffer JS
   interop (one flush per frame), MSDF text from a shared glyph atlas (own managed rasterizer,
   no webfonts), optional pre-baked `.sdfg` atlas for zero-rasterization startup.
2. **`<WebGlCanvas>`** — hi-dpi canvas host component: sizes the backing buffer to the laid-out
   CSS box x devicePixelRatio, reports `CanvasMetrics` via `OnReady` (create your renderer there)
   / `OnResized` (ResizeObserver + dpr-change watcher), maps pointer coords into backing space,
   and applies gesture CSS (`touch-action:none; user-select:none`) inline. Works for a CPU
   `putImageData` pipeline too — it's renderer-agnostic; the parent owns renderer creation.

Reference consumers, most polished first:
- `sebgod/chess` `Chess.Web/Pages/Play.razor` — dual renderer (WebGL default, `?renderer=cpu`
  software fallback), startup menu, live resize, AOT deploy (`-p:RunAOTCompilation=true` in
  pages.yml only — 24-42x on compute vs interpreted, costs a few MB payload + CI minutes).
  Live: sebgod.github.io/chess.
- `sharpastro/tianwen` TianWen.UI.Web (`feat/web-showcase`) — the pattern's origin, migrating to
  the shared component.

Both are GitHub Pages deploys from the same workflow shape as dotcc's (base-href rewrite, 404
fallback, .nojekyll) — chess's `.github/workflows/pages.yml` is the current best template.
