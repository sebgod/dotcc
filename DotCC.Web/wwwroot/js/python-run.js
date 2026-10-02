// Run a Python program on python.wasm, CPython 3.13 that dotcc compiled with its wat back end
// (examples/cpython/build-wat.sh), over a file system held in memory: what the sandbox's Python
// mode does in its worker (python-worker.js), and what node can do too, for testing.
//
// The standard library comes as a pack (examples/cpython/pack-stdlib.py), unpacked under the
// interpreter's home, /py (PYTHONHOME), at /py/lib/python3.13. A program is written to /main.py
// and run as `python /main.py`, so a traceback shows its lines; /tmp is there for tempfile, and
// whatever else the program writes stays in memory. Each run is a fresh instance of the module
// (a fresh interpreter); the file system is kept, less the program's own files, so the bytecode
// CPython caches beside the library on one run serves the next.
//
//   const files = PythonRun.unpack(await PythonRun.inflate(packBytes));
//   const fs = new WasiFs.MemFs(); PythonRun.mount(fs, files);
//   const status = PythonRun.run(module, fs, code, { stdout: (bytes) => ..., stderr: (bytes) => ... });
(function (root) {
  'use strict';
  const WasiFs = root.WasiFs || (typeof require === 'function' ? require('./wasi-fs.js') : null);

  const STDLIB = 'py/lib/python3.13';

  /** A pack's bytes, gzip as pack-stdlib.py writes it or already inflated (by a host that
   *  decoded the HTTP response). */
  async function inflate(bytes) {
    if (bytes[0] !== 0x1f || bytes[1] !== 0x8b) { return bytes; }
    const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));
    return new Uint8Array(await new Response(stream).arrayBuffer());
  }

  /** The files of an inflated pack: [{ path, data }], data a view into the pack's bytes. */
  function unpack(raw) {
    const magic = new TextDecoder().decode(raw.subarray(0, 8));
    if (magic !== 'DOTCCPK1') { throw new Error('not a dotcc stdlib pack'); }
    const view = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
    const headerLen = view.getUint32(8, true);
    const header = JSON.parse(new TextDecoder().decode(raw.subarray(12, 12 + headerLen)));
    let at = 12 + headerLen;
    return header.map(([path, size]) => {
      const data = raw.subarray(at, at + size);
      at += size;
      return { path, data };
    });
  }

  /** Put the standard library's files under the home in fs. */
  function mount(fs, files) {
    for (const f of files) { fs.addFile(`${STDLIB}/${f.path}`, f.data); }
  }

  class Exit { constructor(code) { this.code = code; } }

  /** Run code as /main.py on a fresh instance of module over fs; its status (main's value, or
   *  exit's). io.stdout and io.stderr get what it writes, as bytes; io.sleep(ms), when given,
   *  blocks for a sleep (else a busy wait). A trap is thrown. */
  function run(module, fs, code, io) {
    for (const name of [...fs.root.entries.keys()]) {
      if (name !== 'py') { fs.root.entries.delete(name); }
    }
    fs.addFile('main.py', code);
    fs.mkdir('tmp');
    let instance;
    const memory = () => instance.exports.memory.buffer;
    const wasi = WasiFs.createWasi({
      memory,
      args: ['python', '/main.py'],
      // The output pane shows text, not a terminal's escape codes: no colored tracebacks.
      env: { PYTHONHOME: '/py', HOME: '/', TMPDIR: '/tmp', NO_COLOR: '1' },
      preopens: [{ name: '/', fs }],
      stdout: io.stdout,
      stderr: io.stderr,
      stdin: () => null,
      sleep: io.sleep,
    });
    const randomFill = (bytes) => {
      if (typeof crypto !== 'undefined' && crypto.getRandomValues) {
        for (let at = 0; at < bytes.length; at += 65536) { crypto.getRandomValues(bytes.subarray(at, Math.min(at + 65536, bytes.length))); }
      } else {
        require('crypto').randomFillSync(bytes);
      }
    };
    const start = typeof performance !== 'undefined' ? performance.now() : 0;
    const preview1 = {
      ...wasi,
      proc_exit(status) { throw new Exit(status); },
      // Clock 0 is the time of day; the others (monotonic, CPU time) count from the run's start.
      clock_time_get(id, precision, timePtr) {
        if (id < 0 || id > 3) { return 28; }
        const ns = id === 0 ? BigInt(Date.now()) * 1000000n : BigInt(Math.round((performance.now() - start) * 1e6));
        new DataView(memory()).setBigUint64(timePtr, ns, true);
        return 0;
      },
      clock_res_get(id, resPtr) {
        if (id < 0 || id > 3) { return 28; }
        new DataView(memory()).setBigUint64(resPtr, id === 0 ? 1000000n : 1000n, true);
        return 0;
      },
      random_get(buf, len) {
        const bytes = new Uint8Array(len);
        randomFill(bytes);
        new Uint8Array(memory(), buf, len).set(bytes);
        return 0;
      },
    };
    instance = new WebAssembly.Instance(module, { wasi_snapshot_preview1: preview1 });
    try {
      instance.exports._start();
      return 0;
    } catch (e) {
      if (e instanceof Exit) { return e.code; }
      throw e;
    }
  }

  const api = { inflate, unpack, mount, run };
  if (typeof module !== 'undefined' && module.exports) { module.exports = api; }
  else { root.PythonRun = api; }
})(typeof globalThis !== 'undefined' ? globalThis : this);
