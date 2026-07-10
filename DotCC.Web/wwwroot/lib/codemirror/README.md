# Vendored `dotcc-codemirror.js` (the source editor)

`dotcc-codemirror.js` is a **self-contained [CodeMirror 6](https://codemirror.net/)
bundle**, loaded via a plain `<script>` in `index.html`, exposing a small imperative
global `window.dotccEditor` (`create` / `getValue` / `setValue`) that `Home.razor`
drives through JS interop. CodeMirror 6 ships as ES modules meant to be bundled by the
consuming app; there is no official pre-built browser file, so we bundle our own with
esbuild and commit the result — no CDN, no ESM loader (fable-web.md D3). The bundle *is*
the reference; the recipe below reproduces it byte-for-similar.

- **Packages (npm, all MIT):**
  - `@codemirror/view` **6.43.6**
  - `@codemirror/state` **6.7.1**
  - `@codemirror/commands` **6.10.4**
  - `@codemirror/language` **6.12.4**
  - `@codemirror/lang-cpp` **6.0.3** (C/C++ highlighting)
  - `@codemirror/theme-one-dark` **6.1.3** (dark theme matching the sandbox)
- **License:** MIT (see `LICENSE` alongside — same text for every `@codemirror/*`).
- **Size:** ~410 KB minified (one file, no source map shipped).

## Regenerate (pinned, offline-safe)

In a scratch dir, with these two files:

**`package.json`**
```json
{
  "name": "dotcc-codemirror-bundle",
  "private": true,
  "type": "module",
  "dependencies": {
    "@codemirror/commands": "^6",
    "@codemirror/lang-cpp": "^6",
    "@codemirror/language": "^6",
    "@codemirror/state": "^6",
    "@codemirror/theme-one-dark": "^6",
    "@codemirror/view": "^6"
  }
}
```

**`entry.mjs`** — imports `EditorView`/`EditorState`, the default + history keymaps
(with `indentWithTab`), `cpp()`, `oneDark`, line numbers, active-line + bracket
matching, 4-space indent and line wrapping; assigns `window.dotccEditor = { create,
getValue, setValue }` where `create(id, doc)` mounts an `EditorView` in element `id`,
`getValue(id)` returns `state.doc.toString()`, and `setValue(id, text)` dispatches a
full-document replace. (The canonical `entry.mjs` is kept with the other WEB build
scratch; it is ~45 lines.)

Then:

```bash
npm install --no-audit --no-fund
npx esbuild entry.mjs --bundle --format=iife --minify \
  --legal-comments=none --target=es2020 \
  --outfile=dotcc-codemirror.js
cp dotcc-codemirror.js               DotCC.Web/wwwroot/lib/codemirror/dotcc-codemirror.js
cp node_modules/@codemirror/view/LICENSE  DotCC.Web/wwwroot/lib/codemirror/LICENSE
```

Bump versions deliberately (CodeMirror 6 is API-stable within 6.x). If the editor
surface (`create`/`getValue`/`setValue`) ever changes, update `entry.mjs` and
`Home.razor`'s interop calls together.
