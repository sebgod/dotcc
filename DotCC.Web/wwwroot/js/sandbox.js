// dotcc web sandbox — the JS half of the run pipeline (fable-web.md WEB1).
//
// Blazor lowers C/Zig to WebAssembly-text via Compiler.EmitWat; this module turns
// that .wat into a running program entirely in the browser:
//   wat text --> libwabt.js parseWat().toBinary() --> WebAssembly.instantiate
//   --> call main(), capturing what it writes to fd 1/2 through a WASI fd_write shim.
// The shim is the exact one the always-on WatOracleTests use, ported verbatim —
// fd_write is the only import dotcc's wat backend emits.
//
// `WabtModule` is the global exposed by the vendored lib/wabt/libwabt.js (a UMD
// build; with no CommonJS/AMD present it lands on window). It is a function that
// resolves to the wabt API.
window.dotccSandbox = (function () {
  let wabtPromise = null;
  // The most recently assembled wasm binary (Uint8Array), kept so the sandbox can
  // probe it (Compiler.ProbeWasm — the read-only WF0 inventory, fable-web.md WEB6)
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
   * sign-extension + non-trapping float→int + bulk-memory + mutable globals.
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

    // Minimal WASI: fd_write reads each iovec out of the module's exported memory
    // and accumulates fd-1 (stdout) / fd-2 (stderr) bytes; proc_exit unwinds with
    // the status so `main` can early-exit. Any other import the module declares but
    // we don't provide would surface as a link error in the run stage.
    const fd_write = (fd, iovs, iovsLen, nwrittenPtr) => {
      const mem = inst.exports.memory.buffer;
      const dv = new DataView(mem);
      const bytes = new Uint8Array(mem);
      let written = 0;
      for (let i = 0; i < iovsLen; i++) {
        const ptr = dv.getUint32(iovs + i * 8, true);
        const len = dv.getUint32(iovs + i * 8 + 4, true);
        const sink = fd === 2 ? fd2 : fd1;
        for (let j = 0; j < len; j++) { sink.push(bytes[ptr + j]); }
        written += len;
      }
      dv.setUint32(nwrittenPtr, written, true);
      return 0;
    };
    const proc_exit = (code) => { const e = new Error("proc_exit"); e.__exit = code | 0; throw e; };

    const decode = (arr) => new TextDecoder("utf-8", { fatal: false }).decode(new Uint8Array(arr));

    try {
      const { instance } = await WebAssembly.instantiate(buffer, {
        wasi_snapshot_preview1: { fd_write, proc_exit },
      });
      inst = instance;

      let exitCode = 0;
      try {
        const main = inst.exports.main;
        exitCode = typeof main === "function" ? (main() | 0) : 0;
      } catch (e) {
        if (e && typeof e.__exit === "number") { exitCode = e.__exit; }
        else { throw e; }
      }
      return { ok: true, exitCode, stdout: decode(fd1), stderr: decode(fd2) };
    } catch (e) {
      return { ok: false, stage: "run", error: String((e && e.message) || e) };
    }
  }

  // --- share-links (WEB2) -------------------------------------------------
  // Source is deflate-compressed and base64url-packed into the URL fragment, so
  // a playground link is fully self-contained — no server, no storage, no dep.
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

  /** Trigger a browser download of the last assembled wasm binary. Returns false
   *  when there is nothing to download (no successful assemble yet). */
  function downloadLastWasm(filename) {
    if (!lastWasm) { return false; }
    const blob = new Blob([lastWasm], { type: "application/wasm" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename || "sandbox.wasm";
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    return true;
  }

  return { assembleAndRun, makeShareLink, readShareSource, getLastWasmBase64, downloadLastWasm };
})();
