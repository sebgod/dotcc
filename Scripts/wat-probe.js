// Run one dotcc --target=wat module under node, for Scripts/wat-probe.sh: instantiate it with
// a WASI preview1 shim (fd_write to stdout/stderr, proc_exit, clock_time_get, random_get), call main, and write what it
// printed to fd 1 to our stdout, byte for byte.
//   node wat-probe.js module.wasm             run it (exit: main's value & 0xff, 134 on a trap)
//   node wat-probe.js --same expected actual  exit 0 when the two outputs match as the
//                                             fixture tests compare them (\n line endings,
//                                             trailing newlines trimmed)
const fs = require('fs');
if (process.argv[2] === '--same') {
  const norm = (f) => fs.readFileSync(f).toString('latin1').replace(/\r\n/g, '\n').replace(/\n+$/, '');
  process.exit(norm(process.argv[3]) === norm(process.argv[4]) ? 0 : 1);
}
let inst;
const out = [];
const err = [];
class Exit { constructor(code) { this.code = code; } }
const wasi = {
  fd_write(fd, iovs, iovsLen, nwrittenPtr) {
    const dv = new DataView(inst.exports.memory.buffer);
    const bytes = new Uint8Array(inst.exports.memory.buffer);
    let written = 0;
    for (let i = 0; i < iovsLen; i++) {
      const ptr = dv.getUint32(iovs + i * 8, true);
      const len = dv.getUint32(iovs + i * 8 + 4, true);
      const sink = fd === 2 ? err : out;
      for (let j = 0; j < len; j++) sink.push(bytes[ptr + j]);
      written += len;
    }
    dv.setUint32(nwrittenPtr, written, true);
    return 0;
  },
  proc_exit(code) { throw new Exit(code); },
  // clock ids: 0 realtime, 1 monotonic, 2 process CPU time, 3 thread CPU time (nanoseconds).
  clock_time_get(id, precision, timePtr) {
    let ns;
    if (id === 0) { ns = BigInt(Date.now()) * 1000000n; }
    else if (id === 1) { ns = process.hrtime.bigint(); }
    else if (id === 2 || id === 3) { const u = process.cpuUsage(); ns = BigInt(u.user + u.system) * 1000n; }
    else { return 28; }   // EINVAL
    new DataView(inst.exports.memory.buffer).setBigUint64(timePtr, ns, true);
    return 0;
  },
  random_get(buf, len) {
    require('crypto').randomFillSync(new Uint8Array(inst.exports.memory.buffer, buf, len));
    return 0;
  },
};
let code = 0;
try {
  const mod = new WebAssembly.Module(fs.readFileSync(process.argv[2]));
  const imports = {};
  for (const imp of WebAssembly.Module.imports(mod)) {
    if (imp.module === 'wasi_snapshot_preview1' && wasi[imp.name]) {
      (imports[imp.module] ??= {})[imp.name] = wasi[imp.name];
    } else {
      process.stderr.write(`unresolved import ${imp.module}.${imp.name}\n`);
      process.exit(127);
    }
  }
  inst = new WebAssembly.Instance(mod, imports);
  const v = inst.exports.main();
  code = typeof v === 'bigint' ? Number(v & 0xffn) : (v ?? 0) & 0xff;
} catch (e) {
  if (e instanceof Exit) { code = e.code & 0xff; }
  else { process.stderr.write(`trap: ${e && e.message}\n`); code = 134; }
}
process.stdout.write(Buffer.from(out));
process.stderr.write(Buffer.from(err));
process.exit(code);
