# Vendored `libwabt.js` (the wat → wasm assembler)

`libwabt.js` is the **[wabt.js](https://github.com/AssemblyScript/wabt.js)** browser
build (WebAssembly Binary Toolkit, ported to the Web) — self-contained, loaded via a
plain `<script>` in `index.html`, exposing the global `WabtModule()`. The sandbox
uses it to assemble dotcc's emitted `.wat` into a wasm binary client-side
(`parseWat(...).toBinary()`), so the run pipeline needs no server and no CDN
(fable-web.md D3).

- **Package:** `wabt` on npm — **version 1.0.36** (matches the `wat2wasm` CLI the
  always-on wat oracle pins, so browser and CI assemble identically).
- **License:** Apache-2.0 (see `LICENSE` alongside).

## Regenerate (pinned, offline-safe)

```bash
npm pack wabt@1.0.36            # or: npm i wabt@1.0.36 in a scratch dir
# extract, then copy its index.js + LICENSE here:
cp node_modules/wabt/index.js  DotCC.Web/wwwroot/lib/wabt/libwabt.js
cp node_modules/wabt/LICENSE   DotCC.Web/wwwroot/lib/wabt/LICENSE
```

Bump the version deliberately: it only needs to keep up with the wat features
dotcc's backend emits (MVP + sign-extension + non-trapping float→int + bulk-memory
+ mutable-globals — see the WF0 surface-probe report), which wabt has supported for
years. Keep it in lockstep with the `wat2wasm` version the wat oracle uses.
