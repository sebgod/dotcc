// dotcc web sandbox — CodeMirror 6 editor bundle entry (fable-web.md WEB2 slice B).
//
// Bundled by esbuild into a single self-contained IIFE (no CDN, no ESM loader —
// D3). Exposes a tiny imperative surface on `window.dotccEditor` that Home.razor
// drives via JS interop: create the editor, pull its text on Run/Share, push
// text on example-select / share-link load.
import { EditorView, keymap, lineNumbers, highlightActiveLine, drawSelection } from "@codemirror/view";
import { EditorState } from "@codemirror/state";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { indentUnit, bracketMatching } from "@codemirror/language";
import { cpp } from "@codemirror/lang-cpp";
import { oneDark } from "@codemirror/theme-one-dark";

const views = new Map();

/** Create (or re-create) a CodeMirror C editor inside the element `id`. */
function create(id, doc) {
  const parent = document.getElementById(id);
  if (!parent) { return; }
  if (views.has(id)) { views.get(id).destroy(); views.delete(id); }
  const state = EditorState.create({
    doc: doc || "",
    extensions: [
      lineNumbers(),
      highlightActiveLine(),
      drawSelection(),
      history(),
      bracketMatching(),
      indentUnit.of("    "),
      keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap]),
      cpp(),
      oneDark,
      EditorView.lineWrapping,
    ],
  });
  views.set(id, new EditorView({ state, parent }));
}

/** Current document text (empty string if the editor isn't up). */
function getValue(id) {
  const v = views.get(id);
  return v ? v.state.doc.toString() : "";
}

/** Replace the whole document (example dropdown / shared link). */
function setValue(id, text) {
  const v = views.get(id);
  if (!v) { return; }
  v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: text || "" } });
}

window.dotccEditor = { create, getValue, setValue };
