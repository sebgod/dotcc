// The sandbox's Python mode runs here, off the page's thread: a program that loops forever
// keeps this worker busy, not the tab, and the page's Stop terminates it. On its first program
// the worker fetches python.wasm and the standard library's pack (the browser caches both),
// compiles the one and unpacks the other into an in-memory file system (python-run.js), and
// keeps both for the programs after it.
//
// Messages in:  { code, urls: { wasm, stdlib } }
// Messages out: { type: 'loading' } before a first fetch, { type: 'out', fd, text } as the
//               program writes to fd 1 or 2, then { type: 'exit', code } or { type: 'error', error }.
importScripts('wasi-fs.js', 'python-run.js');

let loaded = null;

function load(urls) {
  if (!loaded) {
    postMessage({ type: 'loading' });
    loaded = (async () => {
      const [wasm, pack] = await Promise.all([fetch(urls.wasm), fetch(urls.stdlib)]);
      if (!wasm.ok) { throw new Error(`python.wasm: HTTP ${wasm.status}`); }
      if (!pack.ok) { throw new Error(`the standard library: HTTP ${pack.status}`); }
      const module = await WebAssembly.compile(await wasm.arrayBuffer());
      const files = PythonRun.unpack(await PythonRun.inflate(new Uint8Array(await pack.arrayBuffer())));
      const fs = new WasiFs.MemFs();
      PythonRun.mount(fs, files);
      return { module, fs };
    })();
    loaded.catch(() => { loaded = null; });
  }
  return loaded;
}

onmessage = async ({ data }) => {
  try {
    const { module, fs } = await load(data.urls);
    // UTF-8 across writes: a character a write splits arrives whole with the next.
    const decoders = { 1: new TextDecoder(), 2: new TextDecoder() };
    const emit = (fd) => (bytes) => postMessage({ type: 'out', fd, text: decoders[fd].decode(bytes, { stream: true }) });
    const code = PythonRun.run(module, fs, data.code, { stdout: emit(1), stderr: emit(2) });
    postMessage({ type: 'exit', code });
  } catch (e) {
    postMessage({ type: 'error', error: String((e && e.message) || e) });
  }
};
