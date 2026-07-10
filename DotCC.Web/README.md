# DotCC.Web — the in-browser sandbox

A **fully static, single-page** web app that runs the dotcc compiler **entirely in
the browser** and executes the C (or Zig) you type — no server, no backend, no
telemetry. Live at **<https://sebgod.github.io/dotcc/>**.

It's a Blazor WebAssembly app that loads `DotCC.Lib` (the actual compiler) as managed
`.wasm` and drives this pipeline client-side:

```
source  ─Compiler.EmitWat→  .wat text
        ─libwabt.js parseWat().toBinary()→  wasm bytes
        ─WebAssembly.instantiate + fd_write/proc_exit shim→  run main(), capture stdout/stderr
```

That run path is the always-on `WatOracleTests` round-trip moved into the browser —
`fd_write` is the only import dotcc's wat backend emits. The **wat / C# / -E** tabs
are pure `Compiler.EmitWat` / `EmitCSharp` / `Preprocess` string projections; the
editor is CodeMirror 6; share-links pack the source into a `#src=…` fragment with the
native `CompressionStream` API. Everything is client-side — the only "server" is
GitHub Pages handing over static files.

> **Design note:** this project is deliberately **outside `dotcc.sln` and Central
> Package Management** (it references `DotCC.Lib` directly, opts out of CPM,
> `IsPackable=false`). That keeps the main `ci` workflow from ever building it — its
> sole CI is the `pages` deploy workflow below. See `docs/plans/fable-web.md` for the
> full campaign rationale.

## Run it locally

```bash
dotnet run --project DotCC.Web -c Release
```

Then open the URL it prints (the launch profile uses `http://localhost:5096`). To
pick a different port (e.g. if 5096 is taken), ignore the profile and set the URL
yourself:

```bash
ASPNETCORE_URLS="http://localhost:5137" \
  dotnet run --project DotCC.Web -c Release --no-launch-profile
```

The `<base href="/" />` in `wwwroot/index.html` is what local dev wants; the deploy
rewrites it (see below), so **do not** hard-code `/dotcc/` in the source.

## How the GitHub Pages deployment is set up

Two pieces: a **deploy workflow** and a **one-time Pages enablement**. Both are
already in place — this section documents them so the setup is reproducible.

### 1. One-time: enable Pages with GitHub Actions as the source

No clicking through repo Settings — the REST API sets the source to "GitHub Actions":

```bash
gh api -X POST repos/sebgod/dotcc/pages -f build_type=workflow
# already enabled? update instead:
gh api -X PUT  repos/sebgod/dotcc/pages -f build_type=workflow
```

This provisions `https://sebgod.github.io/dotcc/` and lets the `deploy-pages` action
publish to it. Needs admin on the repo. (It's idempotent-ish: POST 409s if the site
already exists — then use PUT.)

### 2. The deploy workflow — `.github/workflows/pages.yml`

Triggers on push to `main` that touches `DotCC.Web/**`, `DotCC.Lib/**`, `DotCC.Libc/**`
or the workflow itself (so the live sandbox tracks the latest compiler), plus manual
`workflow_dispatch`. The **build** job:

1. **`dotnet workload install wasm-tools`** — a Release Blazor-WASM publish relinks the
   runtime natively (Emscripten) to tree-shake it smaller; this workload provides that
   toolchain. (The RID is `browser-wasm`, OS-neutral, so an ubuntu runner publishes it.)
2. **`dotnet publish DotCC.Web -c Release -o publish -p:UseLocalLalrCc=false`** — the
   static site lands in `publish/wwwroot/` (NuGet LALR.CC path, matching the main CI).
3. **Rewrite the base href** in the *published* `index.html` only:
   `sed 's|<base href="/" />|<base href="/dotcc/" />|'`. A project site is served under
   `/dotcc/`, not root; patching only the published copy keeps local dev at `/`.
4. **`touch .nojekyll`** — GitHub Pages runs Jekyll by default, which **strips any
   directory starting with `_`** — including Blazor's `_framework/`. `.nojekyll`
   disables Jekyll. *This is the #1 way Blazor-on-Pages silently breaks; don't remove it.*
5. **`cp index.html 404.html`** — a static host returns 404 for client-side deep links;
   GitHub Pages serves `404.html` for unknown paths, so a copy of the shell makes routes
   resolve (SPA fallback).
6. **`upload-pages-artifact`** the `publish/wwwroot` folder.

The **deploy** job (`needs: build`) runs `actions/deploy-pages@v4` in the
`github-pages` environment. Least-privilege perms (`pages: write`, `id-token: write`)
and single-flight concurrency (`group: pages`, no cancel-in-progress) are set at the
workflow level.

> .NET's **Webcil** assembly format (`.wasm`-wrapped assemblies) means there's no
> `.dll` MIME-type / antivirus problem that used to plague Blazor on static hosts —
> nothing extra needed for that.

### Redeploy

- **Automatically** — merge to `main` touching the paths above.
- **Manually** — `gh workflow run pages.yml` (or the Actions tab → *pages* → *Run
  workflow*).

### Verifying a deploy

The .NET half (`EmitWat` etc.) is covered by the always-on unit + functional suites;
`EmitWat` doesn't change behaviour by being browser-hosted. The **browser** half is
verified by driving the site in headless Edge over CDP (boot → Run → read the output
pane); that harness runs against both a local dev server and the live URL. A standing
CI smoke-check (node + `libwabt` shim over a committed `.wat`) is a planned follow-up.

## Vendored, self-contained dependencies (no CDN)

Both are committed blobs with provenance + regen instructions:

- **`wwwroot/lib/wabt/`** — `libwabt.js` (wabt.js 1.0.36), the wat→wasm assembler.
- **`wwwroot/lib/codemirror/`** — a `dotcc-codemirror.js` esbuild bundle of CodeMirror 6
  (+ `lang-cpp`, `theme-one-dark`), with the `entry.mjs` / `package.json` build inputs.
