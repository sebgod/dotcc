// Run one dotcc --target=wat module under node: instantiate it with a WASI preview1 shim and,
// for a threaded module, wasi-threads (the shared memory it imports as env.memory, and
// thread-spawn, a worker_threads Worker that instantiates the module over that memory and calls
// its wasi_thread_start), then run it through its _start export, as a WASI command is run.
// The arguments, the environment and the file calls are wasi-fs.js's, which the browser
// sandbox runs too; a --dir option preopens a host directory, which the program sees under a
// name of its own. What the program writes to fd 1 and 2 goes straight to our stdout and
// stderr, from every thread, in the order it is written. Scripts/wat-probe.sh and the wat
// oracle tests both run modules through this file.
//   node wat-run.js [options] module.wasm [arg...]
//                                           run it with argv module.wasm arg... (exit: the
//                                           program's status & 0xff, 134 on a trap)
//     --result                              print the program's status (main's value, or
//                                           exit's) in decimal instead of its output
//     --dir HOST[::GUEST]                   preopen directory HOST as GUEST (default: HOST)
//     --env NAME=VALUE                      add NAME to the program's environment (none else)
//     --argv0 NAME                          the program's argv[0] (default: the module's path)
//   node wat-run.js --same expected actual  exit 0 when the two outputs match as the fixture
//                                           tests compare them (\n line endings, trailing
//                                           newlines trimmed)
const fs = require('fs');
const path = require('path');
const { Worker, isMainThread, workerData } = require('worker_threads');

if (isMainThread && process.argv[2] === '--same') {
  const norm = (f) => fs.readFileSync(f).toString('latin1').replace(/\r\n/g, '\n').replace(/\n+$/, '');
  process.exit(norm(process.argv[3]) === norm(process.argv[4]) ? 0 : 1);
}

// wasi-fs.js sits next to this file where the tests copy them both, and in the web sandbox's
// scripts in the repository.
const wasiFs = require(fs.existsSync(path.join(__dirname, 'wasi-fs.js'))
  ? path.join(__dirname, 'wasi-fs.js')
  : path.join(__dirname, '..', 'DotCC.Web', 'wwwroot', 'js', 'wasi-fs.js'));

class Exit { constructor(code) { this.code = code; } }

// The imports for one instance (the main one, or a thread's). `run` is shared by every thread:
// the module, its shared memory (if it imports one), whether fd 1 is discarded (--result), the
// program's arguments, environment and preopened directories, and the counter thread ids come
// from. Each instance has a descriptor table of its own.
function imports(mod, run, getInstance) {
  const memory = () => getInstance().exports.memory.buffer;
  const files = wasiFs.createWasi({
    memory,
    args: run.args,
    env: run.env,
    preopens: run.dirs.map((d) => ({ name: d.guest, fs: new wasiFs.NodeFs(d.host) })),
    stdout: (bytes) => { if (!run.discardStdout) { fs.writeSync(1, bytes); } },
    stderr: (bytes) => { fs.writeSync(2, bytes); },
    // stdin is ours: a read takes what is there, up to n bytes, and null at the end.
    stdin: (n) => {
      const buf = Buffer.alloc(n);
      let got;
      try { got = fs.readSync(0, buf, 0, n, null); }
      catch (e) { if (e.code === 'EOF') { got = 0; } else { throw e; } }
      return got ? buf.subarray(0, got) : null;
    },
    // A sleep (poll_oneoff on a clock) blocks the thread.
    sleep: (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms),
  });
  const wasi = {
    ...files,
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
    clock_res_get(id, resPtr) {
      if (id < 0 || id > 3) { return 28; }
      new DataView(memory()).setBigUint64(resPtr, id === 0 ? 1000000n : 1000n, true);
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
  const argv = process.argv.slice(2);
  let result = false;
  const dirs = [];
  const env = {};
  let argv0 = null;
  while (argv.length && argv[0].startsWith('--')) {
    const opt = argv.shift();
    if (opt === '--result') { result = true; }
    else if (opt === '--dir') {
      const [host, guest] = argv.shift().split('::');
      dirs.push({ host, guest: guest ?? host });
    } else if (opt === '--argv0') {
      argv0 = argv.shift();
    } else if (opt === '--env') {
      const kv = argv.shift();
      const eq = kv.indexOf('=');
      env[kv.slice(0, eq)] = kv.slice(eq + 1);
    } else {
      process.stderr.write(`wat-run.js: unknown option ${opt}\n`);
      process.exit(2);
    }
  }
  const file = argv[0];
  let value = 0;
  try {
    const mod = new WebAssembly.Module(fs.readFileSync(file));
    const run = {
      module: mod,
      discardStdout: result,
      args: argv0 === null ? argv : [argv0, ...argv.slice(1)],
      env,
      dirs,
      nextTid: new Int32Array(new SharedArrayBuffer(4)).fill(1),
      memory: WebAssembly.Module.imports(mod).some((i) => i.module === 'env' && i.name === 'memory')
        ? new WebAssembly.Memory({ initial: 1, maximum: 16384, shared: true })
        : undefined,
    };
    let instance;
    instance = new WebAssembly.Instance(mod, imports(mod, run, () => instance));
    // A program ends in proc_exit with its status; a module that is not one (no _start) has
    // its main called, whose value is the status.
    if (instance.exports._start) { instance.exports._start(); }
    else {
      const v = instance.exports.main();
      value = typeof v === 'bigint' ? Number(v) : (v ?? 0);
    }
  } catch (e) {
    if (e instanceof Exit) { value = e.code; }
    else {
      // WAT_RUN_STACK=1 shows where: the wasm frames, by function name with wat2wasm --debug-names.
      process.stderr.write(`trap: ${e && (process.env.WAT_RUN_STACK ? e.stack : e.message)}\n`);
      process.exit(result ? 1 : 134);
    }
  }
  if (result) { fs.writeSync(1, String(value)); process.exit(0); }
  // The program's end ends it, threads still running or not (C11 5.1.2.2.3).
  process.exit(value & 0xff);
}
