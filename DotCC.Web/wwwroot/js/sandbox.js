// dotcc web sandbox: the JS half of the run pipeline (fable-web.md WEB1).
//
// Blazor lowers C/Zig to WebAssembly-text via Compiler.EmitWat; this module turns
// that .wat into a running program entirely in the browser:
//   wat text --> libwabt.js parseWat().toBinary() --> WebAssembly.instantiate
//   --> call _start, capturing what it writes to fd 1/2.
// Its WASI is wasi-fs.js's, which Scripts/wat-run.js (the node runner the wat oracle and probe
// use) runs too, over a file system in memory instead of node's. The Python mode runs CPython,
// compiled to wasm by dotcc in CI, in a worker (python-worker.js).
//
// `WabtModule` is the global exposed by the vendored lib/wabt/libwabt.js (a UMD
// build; with no CommonJS/AMD present it lands on window). It is a function that
// resolves to the wabt API.
window.dotccSandbox = (function () {
  let wabtPromise = null;
  // The most recently assembled wasm binary (Uint8Array), kept so the sandbox can
  // probe it (Compiler.ProbeWasm, the read-only WF0 inventory, fable-web.md WEB6)
  // and offer it for download. Cleared at the start of every assemble; set only once
  // toBinary() succeeds, so it is null whenever the last compile failed to assemble.
  let lastWasm = null;

  /** Lazily initialise wabt once; reused across runs. */
  function wabt() {
    if (!wabtPromise) {
      if (typeof WabtModule !== "function") {
        return Promise.reject(new Error("libwabt.js not loaded"));
      }
      wabtPromise = WabtModule();
    }
    return wabtPromise;
  }

  /**
   * Assemble a .wat string to a wasm binary and run its `main`.
   * Returns a plain object (JSON-marshalled back to Blazor):
   *   { ok:true, exitCode, stdout, stderr }  on success
   *   { ok:false, stage:"assemble"|"run", error }  otherwise
   * The feature flags match what dotcc's wat backend emits (WF0's histogram):
   * sign-extension + non-trapping float→int + bulk-memory + mutable globals,
   * exception handling (setjmp/longjmp), and threads (atomics, shared memory).
   */
  async function assembleAndRun(wat) {
    let mod = null;
    lastWasm = null;
    try {
      const w = await wabt();
      mod = w.parseWat("sandbox.wat", wat, {
        sign_extension: true,
        sat_float_to_int: true,
        bulk_memory: true,
        mutable_globals: true,
        exceptions: true,
        threads: true,
      });
      const { buffer } = mod.toBinary({ log: false });
      // Keep a copy: toBinary()'s buffer is a view over wabt-owned memory freed by
      // mod.destroy() in the finally, so snapshot it before running.
      lastWasm = buffer.slice();
      return await run(buffer);
    } catch (e) {
      return { ok: false, stage: "assemble", error: String((e && e.message) || e) };
    } finally {
      if (mod) { try { mod.destroy(); } catch { /* best effort */ } }
    }
  }

  async function run(buffer) {
    let inst = null;
    const fd1 = [];
    const fd2 = [];
    const decode = (arr) => new TextDecoder("utf-8", { fatal: false }).decode(new Uint8Array(arr));

    // The program's WASI: its arguments, descriptors and paths are wasi-fs.js's (shared with
    // the node runner the tests use), over a file system in memory preopened as "/", fresh for
    // each run; what it writes to fd 1 and 2 is kept for the output pane, and fd 0 is at its
    // end (the page has no stdin to give). proc_exit unwinds with the status.
    const wasi = WasiFs.createWasi({
      memory: () => inst.exports.memory.buffer,
      args: ["program"],
      preopens: [{ name: "/", fs: new WasiFs.MemFs() }],
      stdout: (bytes) => { for (const b of bytes) { fd1.push(b); } },
      stderr: (bytes) => { for (const b of bytes) { fd2.push(b); } },
      stdin: () => null,
    });
    const proc_exit = (code) => { const e = new Error("proc_exit"); e.__exit = code | 0; throw e; };
    // clock_time_get (time(), clock()): nanoseconds of clock 0 (realtime) or, for the
    // monotonic and CPU-time clocks, the page's high-resolution time since it loaded.
    const clock_time_get = (id, precision, timePtr) => {
      if (id < 0 || id > 3) { return 28; }   // EINVAL
      const ns = id === 0
        ? BigInt(Date.now()) * 1000000n
        : BigInt(Math.round(performance.now() * 1e6));
      new DataView(inst.exports.memory.buffer).setBigUint64(timePtr, ns, true);
      return 0;
    };
    const clock_res_get = (id, resPtr) => {
      if (id < 0 || id > 3) { return 28; }
      new DataView(inst.exports.memory.buffer).setBigUint64(resPtr, id === 0 ? 1000000n : 1000n, true);
      return 0;
    };
    // random_get (getrandom, getentropy): the browser's secure source, 64 KiB at a time.
    const random_get = (buf, len) => {
      for (let at = 0; at < len; at += 65536) {
        crypto.getRandomValues(new Uint8Array(inst.exports.memory.buffer, buf + at, Math.min(65536, len - at)));
      }
      return 0;
    };

    try {
      const mod = await WebAssembly.compile(buffer);
      // A threaded program (C11 <threads.h>) imports a shared memory and wasi-threads'
      // thread-spawn; it needs SharedArrayBuffer, which only a cross-origin isolated page has,
      // and a worker per thread. Say so instead of failing to link.
      if (WebAssembly.Module.imports(mod).some((i) => i.module === "env" && i.name === "memory")) {
        return {
          ok: false, stage: "run",
          error: "This program uses threads, which run as wasm threads over a shared memory; "
            + "the sandbox cannot run them yet (a shared memory needs a cross-origin isolated page).",
        };
      }
      const instance = await WebAssembly.instantiate(mod, {
        wasi_snapshot_preview1: { ...wasi, proc_exit, clock_time_get, clock_res_get, random_get },
      });
      inst = instance;

      // A program is a WASI command: _start runs it and ends in proc_exit with its status.
      let exitCode = 0;
      try {
        const start = inst.exports._start;
        if (typeof start === "function") { start(); }
        else if (typeof inst.exports.main === "function") { exitCode = inst.exports.main() | 0; }
      } catch (e) {
        if (e && typeof e.__exit === "number") { exitCode = e.__exit; }
        else { throw e; }
      }
      return { ok: true, exitCode, stdout: decode(fd1), stderr: decode(fd2) };
    } catch (e) {
      return { ok: false, stage: "run", error: String((e && e.message) || e) };
    }
  }

  // --- Python (GH #269) ---------------------------------------------------
  // CPython 3.13, compiled to wasm by dotcc's wat back end in CI, runs in a worker
  // (python-worker.js): a program that never ends ties up the worker, not the tab, and Stop
  // terminates it. The worker fetches python.wasm and the standard library on its first run and
  // keeps them; a terminated worker is replaced on the next run.
  let pyWorker = null;
  let pyPending = null;

  function pythonWorker() {
    if (!pyWorker) { pyWorker = new Worker(new URL("js/python-worker.js", document.baseURI)); }
    return pyWorker;
  }

  /**
   * Run Python code as /main.py. Resolves like assembleAndRun: { ok:true, exitCode, stdout,
   * stderr } or { ok:false, stage, error }. What the program writes reaches `progress`, a .NET
   * object reference, through its OnPythonOutput(stdout, stderr) at most every 100 ms, so the
   * output pane fills while it runs; OnPythonLoading() says the interpreter is being fetched.
   */
  function runPython(code, progress) {
    return new Promise((resolve) => {
      const out = { 1: "", 2: "" };
      let timer = null;
      const report = () => {
        timer = null;
        if (progress) { progress.invokeMethodAsync("OnPythonOutput", out[1], out[2]); }
      };
      const finish = (result) => {
        if (timer) { clearTimeout(timer); timer = null; }
        pyPending = null;
        resolve(result);
      };
      pyPending = { finish, out };
      const w = pythonWorker();
      w.onmessage = ({ data }) => {
        if (data.type === "loading") {
          if (progress) { progress.invokeMethodAsync("OnPythonLoading"); }
        } else if (data.type === "out") {
          out[data.fd] += data.text;
          if (!timer) { timer = setTimeout(report, 100); }
        } else if (data.type === "exit") {
          finish({ ok: true, exitCode: data.code, stdout: out[1], stderr: out[2] });
        } else if (data.type === "error") {
          finish({ ok: false, stage: "run", error: data.error, stdout: out[1], stderr: out[2] });
        }
      };
      w.onerror = (e) => finish({ ok: false, stage: "run", error: String((e && e.message) || e) });
      w.postMessage({
        code,
        urls: {
          wasm: new URL("python/python.wasm", document.baseURI).href,
          stdlib: new URL("python/stdlib.bin", document.baseURI).href,
        },
      });
    });
  }

  /** Stop the running Python program: terminate its worker (the next run starts another,
   *  which fetches the interpreter from the browser's cache). */
  function stopPython() {
    if (pyWorker) { pyWorker.terminate(); pyWorker = null; }
    if (pyPending) {
      const { finish, out } = pyPending;
      finish({ ok: true, exitCode: 130, stdout: out[1], stderr: out[2] + "\n[stopped]\n" });
    }
  }

  // --- share-links (WEB2) -------------------------------------------------
  // Source is deflate-compressed and base64url-packed into the URL fragment, so
  // a playground link is fully self-contained: no server, no storage, no dependency.
  // Uses the native CompressionStream API (no pako/lz-string vendoring).

  const enc = new TextEncoder();
  const dec = new TextDecoder();

  function b64urlFromBytes(bytes) {
    let s = "";
    for (let i = 0; i < bytes.length; i++) { s += String.fromCharCode(bytes[i]); }
    return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  }

  function bytesFromB64url(b64) {
    const s = b64.replace(/-/g, "+").replace(/_/g, "/");
    const bin = atob(s);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
    return out;
  }

  async function pipe(bytes, stream) {
    const writer = stream.writable.getWriter();
    writer.write(bytes);
    writer.close();
    return new Uint8Array(await new Response(stream.readable).arrayBuffer());
  }

  /** Compress `source` and build a shareable "#src=…" URL; also copies it to the
   *  clipboard (best effort). Returns the URL string. */
  async function makeShareLink(source) {
    const packed = await pipe(enc.encode(source), new CompressionStream("deflate-raw"));
    const url = `${location.origin}${location.pathname}#src=${b64urlFromBytes(packed)}`;
    try { await navigator.clipboard.writeText(url); } catch { /* clipboard may be blocked; URL still returned */ }
    // Reflect it in the address bar too, without adding a history entry.
    try { history.replaceState(null, "", url); } catch { /* non-fatal */ }
    return url;
  }

  /** If the current URL carries a "#src=…" fragment, decompress and return the
   *  source; otherwise return null. */
  async function readShareSource() {
    const m = /[#&]src=([^&]+)/.exec(location.hash);
    if (!m) { return null; }
    try {
      const bytes = await pipe(bytesFromB64url(m[1]), new DecompressionStream("deflate-raw"));
      return dec.decode(bytes);
    } catch { return null; }
  }

  // --- wasm inventory + download (WEB6) -----------------------------------
  // The assembled binary is handed to Blazor as standard base64 (Compiler.ProbeWasm
  // decodes it with Convert.FromBase64String), and offered as a .wasm download.

  /** Standard base64 (padded, +/ alphabet) of a Uint8Array, chunked so a large
   *  buffer doesn't blow String.fromCharCode's argument limit. */
  function b64Std(bytes) {
    let s = "";
    const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) {
      s += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    }
    return btoa(s);
  }

  /** The last assembled wasm binary as base64, or null if the last compile didn't
   *  assemble. Blazor probes this for the wasm tab. */
  function getLastWasmBase64() {
    return lastWasm ? b64Std(lastWasm) : null;
  }

  /** Trigger a browser download of a Blob via a transient object-URL anchor. */
  function downloadBlob(filename, blob) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  /** Trigger a browser download of the last assembled wasm binary. Returns false
   *  when there is nothing to download (no successful assemble yet). */
  function downloadLastWasm(filename) {
    if (!lastWasm) { return false; }
    downloadBlob(filename || "sandbox.wasm", new Blob([lastWasm], { type: "application/wasm" }));
    return true;
  }

  /** Trigger a browser download of a text artifact (e.g. the emitted C#). The
   *  text lives Blazor-side, so it is passed in rather than snapshotted here. */
  function downloadText(filename, text, mime) {
    downloadBlob(filename || "download.txt",
      new Blob([text ?? ""], { type: (mime || "text/plain") + ";charset=utf-8" }));
    return true;
  }

  return { assembleAndRun, runPython, stopPython, makeShareLink, readShareSource, getLastWasmBase64, downloadLastWasm, downloadText };
})();
