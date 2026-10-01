// Run one dotcc --target=wat module under node: instantiate it with a WASI preview1 shim
// (fd_write, fd_read, fd_seek, fd_close, proc_exit, clock_time_get, random_get) and, for a
// threaded module, wasi-threads (the shared memory it imports as env.memory, and thread-spawn,
// a worker_threads Worker that instantiates the module over that memory and calls its
// wasi_thread_start), then call main.
// What the program writes to fd 1 and 2 goes straight to our stdout and stderr, from every
// thread, in the order it is written. Scripts/wat-probe.sh and the wat oracle tests both run
// modules through this file.
//   node wat-run.js module.wasm             run it (exit: main's value & 0xff, 134 on a trap)
//   node wat-run.js --result module.wasm    print main's value (exit's status if it exits) in
//                                           decimal instead of the program's output
//   node wat-run.js --same expected actual  exit 0 when the two outputs match as the fixture
//                                           tests compare them (\n line endings, trailing
//                                           newlines trimmed)
const fs = require('fs');
const { Worker, isMainThread, workerData } = require('worker_threads');

if (isMainThread && process.argv[2] === '--same') {
  const norm = (f) => fs.readFileSync(f).toString('latin1').replace(/\r\n/g, '\n').replace(/\n+$/, '');
  process.exit(norm(process.argv[3]) === norm(process.argv[4]) ? 0 : 1);
}

class Exit { constructor(code) { this.code = code; } }

// The imports for one instance (the main one, or a thread's). `run` is shared by every thread:
// the module, its shared memory (if it imports one), whether fd 1 is discarded (--result), and
// the counter thread ids come from.
function imports(mod, run, getInstance) {
  const memory = () => getInstance().exports.memory.buffer;
  const wasi = {
    fd_write(fd, iovs, iovsLen, nwrittenPtr) {
      const dv = new DataView(memory());
      let written = 0;
      for (let i = 0; i < iovsLen; i++) {
        const ptr = dv.getUint32(iovs + i * 8, true);
        const len = dv.getUint32(iovs + i * 8 + 4, true);
        // Copy out of the (possibly shared) memory before writing.
        const bytes = Buffer.from(new Uint8Array(memory(), ptr, len));
        if (!(run.discardStdout && fd === 1)) { fs.writeSync(fd === 2 ? 2 : 1, bytes); }
        written += len;
      }
      dv.setUint32(nwrittenPtr, written, true);
      return 0;
    },
    // stdin is ours; fd 0 reads it, filling each iovec in turn until a read comes up short.
    fd_read(fd, iovs, iovsLen, nreadPtr) {
      if (fd !== 0) { return 8; }   // EBADF
      const dv = new DataView(memory());
      let total = 0;
      for (let i = 0; i < iovsLen; i++) {
        const ptr = dv.getUint32(iovs + i * 8, true);
        const len = dv.getUint32(iovs + i * 8 + 4, true);
        if (len === 0) { continue; }
        const bytes = Buffer.alloc(len);
        let got;
        try { got = fs.readSync(0, bytes, 0, len, null); }
        catch (e) { if (e.code === 'EOF') { got = 0; } else { return 29; } }   // EIO
        new Uint8Array(memory(), ptr, got).set(bytes.subarray(0, got));
        total += got;
        if (got < len) { break; }
      }
      dv.setUint32(nreadPtr, total, true);
      return 0;
    },
    // The standard streams are a terminal or a pipe: none of them seeks, and closing one is a
    // no-op; there are no other descriptors.
    fd_seek(fd, offset, whence, newOffsetPtr) { return fd <= 2 ? 70 : 8; },   // ESPIPE, EBADF
    fd_close(fd) { return fd <= 2 ? 0 : 8; },
    proc_exit(code) { throw new Exit(code); },
    // clock ids: 0 realtime, 1 monotonic, 2 process CPU time, 3 thread CPU time (nanoseconds).
    clock_time_get(id, precision, timePtr) {
      let ns;
      if (id === 0) { ns = BigInt(Date.now()) * 1000000n; }
      else if (id === 1) { ns = process.hrtime.bigint(); }
      else if (id === 2 || id === 3) { const u = process.cpuUsage(); ns = BigInt(u.user + u.system) * 1000n; }
      else { return 28; }   // EINVAL
      new DataView(memory()).setBigUint64(timePtr, ns, true);
      return 0;
    },
    random_get(buf, len) {
      // A shared buffer cannot be filled in place by randomFillSync: fill a copy.
      const bytes = require('crypto').randomBytes(len);
      new Uint8Array(memory(), buf, len).set(bytes);
      return 0;
    },
  };
  const threads = {
    'thread-spawn'(startArg) {
      const tid = Atomics.add(run.nextTid, 0, 1);
      new Worker(__filename, { workerData: { ...run, tid, startArg } });
      return tid;
    },
  };
  const result = {};
  for (const imp of WebAssembly.Module.imports(mod)) {
    let value;
    if (imp.module === 'wasi_snapshot_preview1') { value = wasi[imp.name]; }
    else if (imp.module === 'wasi') { value = threads[imp.name]; }
    else if (imp.module === 'env' && imp.name === 'memory') { value = run.memory; }
    if (value === undefined) {
      process.stderr.write(`unresolved import ${imp.module}.${imp.name}\n`);
      process.exit(127);
    }
    (result[imp.module] ??= {})[imp.name] = value;
  }
  return result;
}

if (!isMainThread) {
  // A thread: a new instance over the shared memory, entered at wasi_thread_start.
  const run = workerData;
  let instance;
  try {
    instance = new WebAssembly.Instance(run.module, imports(run.module, run, () => instance));
    instance.exports.wasi_thread_start(run.tid, run.startArg);
  } catch (e) {
    // exit() on a thread, or a trap, ends the program: the main thread may be blocked waiting.
    const code = e instanceof Exit ? e.code & 0xff : 134;
    if (!(e instanceof Exit)) { fs.writeSync(2, `trap in thread ${run.tid}: ${e && e.message}\n`); }
    process.kill(process.pid, 'SIGTERM');
    process.exit(code);
  }
} else {
  const result = process.argv[2] === '--result';
  const file = process.argv[result ? 3 : 2];
  let code = 0;
  let value = 0;
  try {
    const mod = new WebAssembly.Module(fs.readFileSync(file));
    const run = {
      module: mod,
      discardStdout: result,
      nextTid: new Int32Array(new SharedArrayBuffer(4)).fill(1),
      memory: WebAssembly.Module.imports(mod).some((i) => i.module === 'env' && i.name === 'memory')
        ? new WebAssembly.Memory({ initial: 1, maximum: 16384, shared: true })
        : undefined,
    };
    let instance;
    instance = new WebAssembly.Instance(mod, imports(mod, run, () => instance));
    const v = instance.exports.main();
    value = typeof v === 'bigint' ? Number(v) : (v ?? 0);
    code = value & 0xff;
  } catch (e) {
    if (e instanceof Exit) { value = e.code; code = e.code & 0xff; }
    else { process.stderr.write(`trap: ${e && e.message}\n`); process.exit(result ? 1 : 134); }
  }
  if (result) { fs.writeSync(1, String(value)); process.exit(0); }
  // Returning from main ends the program, threads still running or not (C11 5.1.2.2.3).
  process.exit(code);
}
