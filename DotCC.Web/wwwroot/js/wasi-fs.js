// The WASI preview1 calls that a dotcc --target=wat program's libc makes about its arguments, its
// environment, and its file descriptors and paths, written once for both of the hosts that run
// those programs: Scripts/wat-run.js under node (the tests, the probes) and the browser sandbox.
// The calls marshal WASI's structures in and out of the module's memory, keep the descriptor
// table (the standard streams, the preopened directories, and what the program opens), and
// resolve each path against the directory it is relative to; the files themselves are a
// driver's. There are two:
//   - NodeFs, a directory of the host's own, through node's fs (synchronous calls), and
//   - MemFs, a tree held in memory, which the browser fills from what it fetches; a file can
//     be a stub that loads its bytes on first read.
// A driver names a file by a path relative to its root, '/'-separated with no '.' or '..'
// ('' is the root), and reports a failure by throwing an Error whose code is a POSIX name
// ('ENOENT'), as node's fs does. What the module does not do: rights (every descriptor may do
// everything its access mode allows), sockets, and waiting on a descriptor in poll_oneoff
// (a descriptor is always ready; a clock subscription sleeps).
//
//   const wasi = createWasi({ memory: () => instance.exports.memory.buffer, args, env,
//     preopens: [{ name: '/', fs: new NodeFs('C:/some/dir') }],
//     stdout: (bytes) => ..., stderr: (bytes) => ..., stdin: (n) => bytes or null at the end });
//   imports.wasi_snapshot_preview1 = { ...wasi, proc_exit, clock_time_get, random_get };
(function (root) {
  'use strict';

  // WASI's errno numbers, by the POSIX name a driver throws.
  const ERRNO = {
    E2BIG: 1, EACCES: 2, EAGAIN: 6, EBADF: 8, EBUSY: 10, EEXIST: 20, EFAULT: 21, EFBIG: 22,
    EINTR: 27, EINVAL: 28, EIO: 29, EISDIR: 31, ELOOP: 32, EMFILE: 33, EMLINK: 34,
    ENAMETOOLONG: 37, ENFILE: 41, ENODEV: 43, ENOENT: 44, ENOMEM: 48, ENOSPC: 51, ENOSYS: 52,
    ENOTDIR: 54, ENOTEMPTY: 55, ENOTSUP: 58, ENOTTY: 59, ENXIO: 60, EOVERFLOW: 61, EPERM: 63,
    EPIPE: 64, EROFS: 69, ESPIPE: 70, EXDEV: 75, ENOTCAPABLE: 76,
  };

  // WASI's file types.
  const FT = { unknown: 0, block: 1, char: 2, dir: 3, file: 4, socket: 6, symlink: 7, fifo: 0 };

  // Rights: every one (WASI preview1 has 30), and the two that say a descriptor reads or writes.
  const RIGHTS_ALL = 0x3fffffffn;
  const RIGHT_FD_READ = 1n << 1n;
  const RIGHT_FD_WRITE = 1n << 6n;
  // A stream (the standard ones) neither seeks nor tells, which is how a program tells it is a
  // terminal (wasi-libc's isatty).
  const RIGHTS_STREAM = RIGHTS_ALL & ~((1n << 2n) | (1n << 5n));

  const FDFLAG_APPEND = 1;
  const OFLAG_CREAT = 1, OFLAG_DIRECTORY = 2, OFLAG_EXCL = 4, OFLAG_TRUNC = 8;
  const LOOKUP_FOLLOW = 1;
  const FST_ATIM = 1, FST_ATIM_NOW = 2, FST_MTIM = 4, FST_MTIM_NOW = 8;

  class FsError extends Error {
    constructor(code) { super(code); this.code = code; }
  }
  const fail = (code) => { throw new FsError(code); };

  const encoder = new TextEncoder();
  const decoder = new TextDecoder();
  const nowNs = () => BigInt(Date.now()) * 1000000n;

  // ---- the descriptor table and the calls ----------------------------------------------------

  function createWasi(opts) {
    const memory = opts.memory;
    const args = (opts.args || []).map((a) => encoder.encode(a + '\0'));
    const env = Object.entries(opts.env || {}).map(([k, v]) => encoder.encode(`${k}=${v}\0`));
    const view = () => new DataView(memory());
    const bytes = (ptr, len) => new Uint8Array(memory(), ptr, len);
    const str = (ptr, len) => decoder.decode(bytes(ptr, len).slice());

    // A descriptor: a stream (the standard ones), or a file or directory of a driver, at a path
    // relative to the driver's root; a preopened directory also has the name the program sees.
    const fds = [];
    fds[0] = { kind: 'stream', read: opts.stdin || (() => null) };
    fds[1] = { kind: 'stream', write: opts.stdout || (() => {}) };
    fds[2] = { kind: 'stream', write: opts.stderr || (() => {}) };
    for (const p of opts.preopens || []) {
      fds.push({ kind: 'dir', fs: p.fs, path: '', preopen: p.name, rights: RIGHTS_ALL, flags: 0 });
    }
    const lowestFree = () => { let i = 3; while (fds[i]) { i++; } return i; };

    // Run a call: its result is 0, or the errno a driver's failure names (EIO for any other).
    const call = (f) => {
      try { return f() ?? 0; }
      catch (e) {
        if (e && typeof e.code === 'string') { return ERRNO[e.code] ?? ERRNO.EIO; }
        throw e;
      }
    };
    const fdOf = (fd) => { const d = fds[fd]; if (!d) { fail('EBADF'); } return d; };
    const dirOf = (fd) => { const d = fdOf(fd); if (d.kind !== 'dir') { fail('ENOTDIR'); } return d; };

    // A path relative to directory descriptor dirfd, as the driver names it. WASI's paths do not
    // leave the directory: one that climbs out of it ('..' past its top, or an absolute path)
    // is not the program's to reach.
    function resolve(dirfd, pathPtr, pathLen) {
      const d = dirOf(dirfd);
      const p = str(pathPtr, pathLen);
      if (p.startsWith('/')) { fail('ENOTCAPABLE'); }
      const parts = d.path === '' ? [] : d.path.split('/');
      const top = parts.length;
      for (const seg of p.split('/')) {
        if (seg === '' || seg === '.') { continue; }
        if (seg === '..') {
          if (parts.length === top) { fail('ENOTCAPABLE'); }
          parts.pop();
        } else { parts.push(seg); }
      }
      return { fs: d.fs, path: parts.join('/') };
    }

    function writeFilestat(ptr, st) {
      const v = view();
      v.setBigUint64(ptr, BigInt(st.dev || 0), true);
      v.setBigUint64(ptr + 8, BigInt(st.ino || 0), true);
      v.setUint8(ptr + 16, FT[st.type] ?? 0);
      for (let i = 17; i < 24; i++) { v.setUint8(ptr + i, 0); }
      v.setBigUint64(ptr + 24, BigInt(st.nlink || 1), true);
      v.setBigUint64(ptr + 32, BigInt(st.size || 0), true);
      v.setBigUint64(ptr + 40, BigInt(st.atim || 0), true);
      v.setBigUint64(ptr + 48, BigInt(st.mtim || 0), true);
      v.setBigUint64(ptr + 56, BigInt(st.ctim || 0), true);
    }

    // Copy a list of NUL-terminated strings out, as args_get and environ_get do.
    function putStrings(list, ptrsPtr, bufPtr) {
      const v = view();
      let at = bufPtr;
      list.forEach((s, i) => {
        v.setUint32(ptrsPtr + i * 4, at, true);
        bytes(at, s.length).set(s);
        at += s.length;
      });
      return 0;
    }
    const sizes = (list, countPtr, sizePtr) => {
      const v = view();
      v.setUint32(countPtr, list.length, true);
      v.setUint32(sizePtr, list.reduce((n, s) => n + s.length, 0), true);
      return 0;
    };

    // The iovecs at iovs as [address, length] pairs.
    function iovecs(iovs, n) {
      const v = view();
      const out = [];
      for (let i = 0; i < n; i++) { out.push([v.getUint32(iovs + i * 8, true), v.getUint32(iovs + i * 8 + 4, true)]); }
      return out;
    }

    function readInto(d, iovs, n, position) {
      let total = 0;
      for (const [ptr, len] of iovecs(iovs, n)) {
        if (len === 0) { continue; }
        let got;
        if (d.kind === 'stream') {
          const chunk = d.read ? d.read(len) : null;
          got = chunk ? chunk.length : 0;
          if (got) { bytes(ptr, got).set(chunk.subarray(0, got)); }
        } else {
          if (d.kind === 'dir') { fail('EISDIR'); }
          if (!(d.rights & RIGHT_FD_READ)) { fail('EBADF'); }
          // Read through a copy: a shared memory is not a buffer a driver may fill in place.
          const buf = new Uint8Array(len);
          got = d.fs.read(d.handle, buf, position + total);
          bytes(ptr, got).set(buf.subarray(0, got));
        }
        total += got;
        if (got < len) { break; }
      }
      return total;
    }

    function writeFrom(d, iovs, n, position) {
      let total = 0;
      for (const [ptr, len] of iovecs(iovs, n)) {
        const chunk = bytes(ptr, len).slice();
        if (d.kind === 'stream') {
          if (!d.write) { fail('EBADF'); }
          d.write(chunk);
        } else {
          if (d.kind === 'dir') { fail('EISDIR'); }
          if (!(d.rights & RIGHT_FD_WRITE)) { fail('EBADF'); }
          d.fs.write(d.handle, chunk, position + total);
        }
        total += len;
      }
      return total;
    }

    const wasi = {
      args_sizes_get: (countPtr, sizePtr) => sizes(args, countPtr, sizePtr),
      args_get: (ptrsPtr, bufPtr) => putStrings(args, ptrsPtr, bufPtr),
      environ_sizes_get: (countPtr, sizePtr) => sizes(env, countPtr, sizePtr),
      environ_get: (ptrsPtr, bufPtr) => putStrings(env, ptrsPtr, bufPtr),

      fd_prestat_get: (fd, ptr) => call(() => {
        const d = fdOf(fd);
        if (d.preopen === undefined) { fail('EBADF'); }
        const v = view();
        v.setUint8(ptr, 0);
        v.setUint32(ptr + 4, encoder.encode(d.preopen).length, true);
      }),
      fd_prestat_dir_name: (fd, ptr, len) => call(() => {
        const d = fdOf(fd);
        if (d.preopen === undefined) { fail('EBADF'); }
        const name = encoder.encode(d.preopen);
        if (len < name.length) { fail('ENAMETOOLONG'); }
        bytes(ptr, name.length).set(name);
      }),

      fd_write: (fd, iovs, n, nPtr) => call(() => {
        const d = fdOf(fd);
        let at = d.offset || 0;
        if (d.kind === 'file' && (d.flags & FDFLAG_APPEND)) { at = d.fs.fstat(d.handle).size; }
        const wrote = writeFrom(d, iovs, n, at);
        if (d.kind === 'file') { d.offset = at + wrote; }
        view().setUint32(nPtr, wrote, true);
      }),
      fd_pwrite: (fd, iovs, n, offset, nPtr) => call(() => {
        const d = fdOf(fd);
        if (d.kind !== 'file') { fail('ESPIPE'); }
        view().setUint32(nPtr, writeFrom(d, iovs, n, Number(offset)), true);
      }),
      fd_read: (fd, iovs, n, nPtr) => call(() => {
        const d = fdOf(fd);
        const got = readInto(d, iovs, n, d.offset || 0);
        if (d.kind === 'file') { d.offset += got; }
        view().setUint32(nPtr, got, true);
      }),
      fd_pread: (fd, iovs, n, offset, nPtr) => call(() => {
        const d = fdOf(fd);
        if (d.kind !== 'file') { fail('ESPIPE'); }
        view().setUint32(nPtr, readInto(d, iovs, n, Number(offset)), true);
      }),
      fd_seek: (fd, offset, whence, newPtr) => call(() => {
        const d = fdOf(fd);
        if (d.kind === 'stream') { fail('ESPIPE'); }
        if (d.kind === 'dir') { fail('EBADF'); }
        const base = whence === 0 ? 0 : whence === 1 ? d.offset : whence === 2 ? d.fs.fstat(d.handle).size : fail('EINVAL');
        const to = base + Number(offset);
        if (to < 0) { fail('EINVAL'); }
        d.offset = to;
        view().setBigUint64(newPtr, BigInt(to), true);
      }),
      fd_tell: (fd, ptr) => call(() => {
        const d = fdOf(fd);
        if (d.kind !== 'file') { fail('ESPIPE'); }
        view().setBigUint64(ptr, BigInt(d.offset), true);
      }),
      fd_close: (fd) => call(() => {
        const d = fdOf(fd);
        if (d.handle !== undefined) { d.fs.close(d.handle); }
        delete fds[fd];
      }),
      fd_renumber: (from, to) => call(() => {
        const d = fdOf(from);
        if (fds[to] && fds[to].handle !== undefined) { fds[to].fs.close(fds[to].handle); }
        fds[to] = d;
        delete fds[from];
      }),
      fd_sync: (fd) => call(() => { fdOf(fd); }),
      fd_datasync: (fd) => call(() => { fdOf(fd); }),
      fd_advise: (fd) => call(() => { fdOf(fd); }),
      fd_allocate: (fd, offset, len) => call(() => {
        const d = fdOf(fd);
        if (d.kind !== 'file') { fail('EBADF'); }
        const want = Number(offset) + Number(len);
        if (d.fs.fstat(d.handle).size < want) { d.fs.truncate(d.handle, want); }
      }),
      fd_fdstat_get: (fd, ptr) => call(() => {
        const d = fdOf(fd);
        const v = view();
        const type = d.kind === 'stream' ? FT.char : d.kind === 'dir' ? FT.dir : FT[d.fs.fstat(d.handle).type] ?? 0;
        v.setUint8(ptr, type);
        v.setUint16(ptr + 2, d.flags || 0, true);
        const rights = d.kind === 'stream' ? RIGHTS_STREAM : d.rights;
        v.setBigUint64(ptr + 8, rights, true);
        v.setBigUint64(ptr + 16, RIGHTS_ALL, true);
      }),
      fd_fdstat_set_flags: (fd, flags) => call(() => { fdOf(fd).flags = flags; }),
      fd_fdstat_set_rights: (fd) => call(() => { fdOf(fd); }),
      fd_filestat_get: (fd, ptr) => call(() => {
        const d = fdOf(fd);
        writeFilestat(ptr, d.kind === 'stream' ? { type: 'char', ino: fd } : d.kind === 'dir' ? d.fs.stat(d.path, true) : d.fs.fstat(d.handle));
      }),
      fd_filestat_set_size: (fd, size) => call(() => {
        const d = fdOf(fd);
        if (d.kind !== 'file') { fail('EINVAL'); }
        d.fs.truncate(d.handle, Number(size));
      }),
      fd_filestat_set_times: (fd, atim, mtim, fst) => call(() => {
        const d = fdOf(fd);
        if (d.kind === 'stream') { return; }
        setTimes(d.fs, d.path, atim, mtim, fst);
      }),
      fd_readdir: (fd, buf, bufLen, cookie, usedPtr) => call(() => {
        const d = dirOf(fd);
        const entries = [{ name: '.', type: 'dir' }, { name: '..', type: 'dir' }, ...d.fs.readdir(d.path)];
        let used = 0;
        for (let i = Number(cookie); i < entries.length && used < bufLen; i++) {
          const name = encoder.encode(entries[i].name);
          const head = new Uint8Array(24);
          const hv = new DataView(head.buffer);
          hv.setBigUint64(0, BigInt(i + 1), true);
          hv.setBigUint64(8, BigInt(entries[i].ino || 0), true);
          hv.setUint32(16, name.length, true);
          hv.setUint8(20, FT[entries[i].type] ?? 0);
          // An entry that does not fit is cut off at the buffer's end; the program reads again
          // from its cookie with a larger buffer.
          for (const part of [head, name]) {
            const n = Math.min(part.length, bufLen - used);
            bytes(buf + used, n).set(part.subarray(0, n));
            used += n;
          }
        }
        view().setUint32(usedPtr, used, true);
      }),

      path_open: (dirfd, lookup, pathPtr, pathLen, oflags, rightsBase, rightsInh, fdflags, fdPtr) => call(() => {
        const { fs, path } = resolve(dirfd, pathPtr, pathLen);
        let st = null;
        try { st = fs.stat(path, (lookup & LOOKUP_FOLLOW) !== 0); }
        catch (e) { if (e.code !== 'ENOENT') { throw e; } }
        if (st && (oflags & OFLAG_CREAT) && (oflags & OFLAG_EXCL)) { fail('EEXIST'); }
        if (!st && !(oflags & OFLAG_CREAT)) { fail('ENOENT'); }
        if (st && st.type === 'symlink') { fail('ELOOP'); }
        const rights = BigInt(rightsBase) & RIGHTS_ALL;
        const writes = (rights & RIGHT_FD_WRITE) !== 0n;
        let d;
        if (st && st.type === 'dir') {
          if (writes && (rights & RIGHT_FD_READ) === 0n) { fail('EISDIR'); }
          d = { kind: 'dir', fs, path, rights, flags: fdflags };
        } else {
          if (oflags & OFLAG_DIRECTORY) { fail(st ? 'ENOTDIR' : 'ENOENT'); }
          const handle = fs.open(path, { create: !st, truncate: (oflags & OFLAG_TRUNC) !== 0, write: writes });
          d = { kind: 'file', fs, path, handle, rights, flags: fdflags, offset: 0 };
        }
        const fd = lowestFree();
        fds[fd] = d;
        view().setUint32(fdPtr, fd, true);
      }),
      path_filestat_get: (dirfd, lookup, pathPtr, pathLen, ptr) => call(() => {
        const { fs, path } = resolve(dirfd, pathPtr, pathLen);
        writeFilestat(ptr, fs.stat(path, (lookup & LOOKUP_FOLLOW) !== 0));
      }),
      path_filestat_set_times: (dirfd, lookup, pathPtr, pathLen, atim, mtim, fst) => call(() => {
        const { fs, path } = resolve(dirfd, pathPtr, pathLen);
        setTimes(fs, path, atim, mtim, fst);
      }),
      path_create_directory: (dirfd, pathPtr, pathLen) => call(() => {
        const { fs, path } = resolve(dirfd, pathPtr, pathLen);
        fs.mkdir(path);
      }),
      path_remove_directory: (dirfd, pathPtr, pathLen) => call(() => {
        const { fs, path } = resolve(dirfd, pathPtr, pathLen);
        fs.rmdir(path);
      }),
      path_unlink_file: (dirfd, pathPtr, pathLen) => call(() => {
        const { fs, path } = resolve(dirfd, pathPtr, pathLen);
        fs.unlink(path);
      }),
      path_rename: (oldfd, oldPtr, oldLen, newfd, newPtr, newLen) => call(() => {
        const from = resolve(oldfd, oldPtr, oldLen);
        const to = resolve(newfd, newPtr, newLen);
        if (from.fs !== to.fs) { fail('EXDEV'); }
        from.fs.rename(from.path, to.path);
      }),
      path_link: (oldfd, lookup, oldPtr, oldLen, newfd, newPtr, newLen) => call(() => {
        const from = resolve(oldfd, oldPtr, oldLen);
        const to = resolve(newfd, newPtr, newLen);
        if (from.fs !== to.fs) { fail('EXDEV'); }
        from.fs.link(from.path, to.path);
      }),
      path_symlink: (targetPtr, targetLen, dirfd, pathPtr, pathLen) => call(() => {
        const { fs, path } = resolve(dirfd, pathPtr, pathLen);
        fs.symlink(str(targetPtr, targetLen), path);
      }),
      path_readlink: (dirfd, pathPtr, pathLen, buf, bufLen, usedPtr) => call(() => {
        const { fs, path } = resolve(dirfd, pathPtr, pathLen);
        const target = encoder.encode(fs.readlink(path));
        const n = Math.min(target.length, bufLen);
        bytes(buf, n).set(target.subarray(0, n));
        view().setUint32(usedPtr, n, true);
      }),

      // Clock subscriptions sleep (the longest of them, relative or absolute); a descriptor is
      // ready at once. Each subscription gets its event.
      poll_oneoff: (inPtr, outPtr, n, countPtr) => call(() => {
        const v = view();
        let sleepNs = 0n;
        for (let i = 0; i < n; i++) {
          const sub = inPtr + i * 48;
          const ev = outPtr + i * 32;
          const tag = v.getUint8(sub + 8);
          v.setBigUint64(ev, v.getBigUint64(sub, true), true);
          v.setUint16(ev + 8, 0, true);
          v.setUint8(ev + 10, tag);
          v.setBigUint64(ev + 16, 0n, true);
          v.setUint16(ev + 24, 0, true);
          if (tag === 0) {
            const timeout = v.getBigUint64(sub + 24, true);
            const abs = (v.getUint16(sub + 40, true) & 1) !== 0;
            const wait = abs ? timeout - nowNs() : timeout;
            if (wait > sleepNs) { sleepNs = wait; }
          }
        }
        if (sleepNs > 0n) { (opts.sleep || busySleep)(Number(sleepNs / 1000000n)); }
        v.setUint32(countPtr, n, true);
      }),
      sched_yield: () => 0,
    };

    function setTimes(fs, path, atim, mtim, fst) {
      const now = nowNs();
      const a = (fst & FST_ATIM_NOW) ? now : (fst & FST_ATIM) ? BigInt(atim) : null;
      const m = (fst & FST_MTIM_NOW) ? now : (fst & FST_MTIM) ? BigInt(mtim) : null;
      fs.utimes(path, a, m);
    }

    return wasi;
  }

  function busySleep(ms) {
    const until = Date.now() + ms;
    while (Date.now() < until) { /* no blocking wait on this thread */ }
  }

  // ---- MemFs: a tree in memory ---------------------------------------------------------------

  // A node is a directory (entries: name to node), a file (data, its first size bytes the
  // file's, or load, which gives them on first use) or a symbolic link (target).
  class MemFs {
    constructor() {
      this.nextIno = 1;
      this.root = this.node('dir');
    }
    node(type, extra) {
      const t = nowNs();
      return Object.assign({ type, ino: this.nextIno++, nlink: 1, atim: t, mtim: t, ctim: t },
        type === 'dir' ? { entries: new Map() } : type === 'file' ? { data: new Uint8Array(0), size: 0 } : {}, extra);
    }
    // Add a file at path (a string or bytes, or a function that gives bytes when first read),
    // making the directories on the way.
    addFile(path, content) {
      const parts = path.split('/').filter((s) => s !== '');
      const name = parts.pop();
      let dir = this.root;
      for (const seg of parts) {
        let next = dir.entries.get(seg);
        if (!next) { next = this.node('dir'); dir.entries.set(seg, next); }
        if (next.type !== 'dir') { fail('ENOTDIR'); }
        dir = next;
      }
      const file = this.node('file');
      if (typeof content === 'function') { file.load = content; file.size = 0; }
      else {
        const data = typeof content === 'string' ? encoder.encode(content) : content;
        file.data = data; file.size = data.length;
      }
      dir.entries.set(name, file);
      return file;
    }
    loaded(n) {
      if (n.load) { const data = n.load(); delete n.load; n.data = data; n.size = data.length; }
      return n;
    }
    // The node at path, following symbolic links (the last one only when follow is set).
    lookup(path, follow, depth = 0) {
      if (depth > 32) { fail('ELOOP'); }
      const parts = path === '' ? [] : path.split('/');
      let n = this.root;
      const at = [];
      for (let i = 0; i < parts.length; i++) {
        if (n.type !== 'dir') { fail('ENOTDIR'); }
        const next = n.entries.get(parts[i]);
        if (!next) { fail('ENOENT'); }
        if (next.type === 'symlink' && (follow || i < parts.length - 1)) {
          const target = this.join(at, next.target);
          n = this.lookup([target, ...parts.slice(i + 1)].filter((s) => s !== '').join('/'), follow, depth + 1);
          return n;
        }
        at.push(parts[i]);
        n = next;
      }
      return n;
    }
    join(base, target) {
      const parts = target.startsWith('/') ? [] : [...base];
      for (const seg of target.split('/')) {
        if (seg === '' || seg === '.') { continue; }
        if (seg === '..') { parts.pop(); } else { parts.push(seg); }
      }
      return parts.join('/');
    }
    parent(path) {
      const parts = path.split('/');
      const name = parts.pop();
      if (!name) { fail('EINVAL'); }
      const dir = this.lookup(parts.join('/'), true);
      if (dir.type !== 'dir') { fail('ENOTDIR'); }
      return { dir, name };
    }
    statOf(n) {
      if (n.type === 'file') { this.loaded(n); }
      return { type: n.type, ino: n.ino, nlink: n.nlink, size: n.type === 'file' ? n.size : n.type === 'symlink' ? n.target.length : 4096, atim: n.atim, mtim: n.mtim, ctim: n.ctim };
    }
    stat(path, follow) { return this.statOf(this.lookup(path, follow)); }
    fstat(h) { return this.statOf(h); }
    open(path, how) {
      let n;
      if (how.create) {
        const { dir, name } = this.parent(path);
        n = this.node('file');
        dir.entries.set(name, n);
      } else {
        n = this.loaded(this.lookup(path, true));
      }
      if (how.truncate) { n.size = 0; n.mtim = nowNs(); }
      return n;
    }
    close() {}
    read(n, buf, position) {
      this.loaded(n);
      const len = Math.max(0, Math.min(buf.length, n.size - position));
      buf.set(n.data.subarray(position, position + len));
      return len;
    }
    write(n, chunk, position) {
      this.loaded(n);
      this.grow(n, position + chunk.length);
      n.data.set(chunk, position);
      n.mtim = nowNs();
      return chunk.length;
    }
    grow(n, size) {
      if (size > n.data.length) {
        const data = new Uint8Array(Math.max(size, n.data.length * 2, 64));
        data.set(n.data.subarray(0, n.size));
        n.data = data;
      }
      if (size > n.size) { n.data.fill(0, n.size, size); n.size = size; }
    }
    truncate(n, size) {
      this.loaded(n);
      if (size > n.size) { this.grow(n, size); } else { n.size = size; }
      n.mtim = nowNs();
    }
    readdir(path) {
      const n = this.lookup(path, true);
      if (n.type !== 'dir') { fail('ENOTDIR'); }
      return [...n.entries].map(([name, e]) => ({ name, type: e.type, ino: e.ino }));
    }
    mkdir(path) {
      const { dir, name } = this.parent(path);
      if (dir.entries.has(name)) { fail('EEXIST'); }
      dir.entries.set(name, this.node('dir'));
    }
    rmdir(path) {
      const { dir, name } = this.parent(path);
      const n = dir.entries.get(name);
      if (!n) { fail('ENOENT'); }
      if (n.type !== 'dir') { fail('ENOTDIR'); }
      if (n.entries.size) { fail('ENOTEMPTY'); }
      dir.entries.delete(name);
    }
    unlink(path) {
      const { dir, name } = this.parent(path);
      const n = dir.entries.get(name);
      if (!n) { fail('ENOENT'); }
      if (n.type === 'dir') { fail('EISDIR'); }
      n.nlink--;
      dir.entries.delete(name);
    }
    rename(from, to) {
      const a = this.parent(from);
      const n = a.dir.entries.get(a.name);
      if (!n) { fail('ENOENT'); }
      const b = this.parent(to);
      const there = b.dir.entries.get(b.name);
      if (there && there.type === 'dir' && n.type !== 'dir') { fail('EISDIR'); }
      if (there && there.type === 'dir' && there.entries.size) { fail('ENOTEMPTY'); }
      a.dir.entries.delete(a.name);
      b.dir.entries.set(b.name, n);
    }
    link(from, to) {
      const n = this.lookup(from, false);
      if (n.type === 'dir') { fail('EPERM'); }
      const { dir, name } = this.parent(to);
      if (dir.entries.has(name)) { fail('EEXIST'); }
      n.nlink++;
      dir.entries.set(name, n);
    }
    symlink(target, path) {
      const { dir, name } = this.parent(path);
      if (dir.entries.has(name)) { fail('EEXIST'); }
      dir.entries.set(name, this.node('symlink', { target }));
    }
    readlink(path) {
      const n = this.lookup(path, false);
      if (n.type !== 'symlink') { fail('EINVAL'); }
      return n.target;
    }
    utimes(path, atim, mtim) {
      const n = this.lookup(path, true);
      if (atim !== null) { n.atim = atim; }
      if (mtim !== null) { n.mtim = mtim; }
    }
  }

  // ---- NodeFs: a directory of the host's ----------------------------------------------------

  class NodeFs {
    constructor(dir) {
      this.fs = require('fs');
      this.dir = dir.replace(/\\/g, '/').replace(/\/+$/, '');
    }
    host(path) { return path === '' ? this.dir : `${this.dir}/${path}`; }
    statOf(s) {
      const type = s.isFile() ? 'file' : s.isDirectory() ? 'dir' : s.isSymbolicLink() ? 'symlink'
        : s.isCharacterDevice() ? 'char' : s.isBlockDevice() ? 'block' : s.isSocket() ? 'socket' : 'unknown';
      // Sizes are numbers (offsets are), the rest as node gives them with bigint: true.
      return { type, dev: s.dev, ino: s.ino, nlink: s.nlink, size: Number(s.size), atim: s.atimeNs, mtim: s.mtimeNs, ctim: s.ctimeNs };
    }
    stat(path, follow) {
      const p = this.host(path);
      return this.statOf(follow ? this.fs.statSync(p, { bigint: true }) : this.fs.lstatSync(p, { bigint: true }));
    }
    fstat(h) { return this.statOf(this.fs.fstatSync(h.fd, { bigint: true })); }
    open(path, how) {
      const c = this.fs.constants;
      let flags = how.write ? c.O_RDWR : c.O_RDONLY;
      if (how.create) { flags |= c.O_CREAT; }
      if (how.truncate) { flags |= c.O_TRUNC; }
      let fd;
      try { fd = this.fs.openSync(this.host(path), flags, 0o666); }
      catch (e) {
        // A file opened for writing that the host only lets us read: open it read-only.
        if (!how.write || e.code !== 'EACCES' && e.code !== 'EPERM') { throw e; }
        fd = this.fs.openSync(this.host(path), c.O_RDONLY);
      }
      return { fd };
    }
    close(h) { this.fs.closeSync(h.fd); }
    read(h, buf, position) { return this.fs.readSync(h.fd, buf, 0, buf.length, position); }
    write(h, chunk, position) { return this.fs.writeSync(h.fd, chunk, 0, chunk.length, position); }
    truncate(h, size) { this.fs.ftruncateSync(h.fd, size); }
    readdir(path) {
      return this.fs.readdirSync(this.host(path), { withFileTypes: true }).map((e) => ({
        name: e.name,
        type: e.isFile() ? 'file' : e.isDirectory() ? 'dir' : e.isSymbolicLink() ? 'symlink' : 'unknown',
      }));
    }
    mkdir(path) { this.fs.mkdirSync(this.host(path)); }
    rmdir(path) { this.fs.rmdirSync(this.host(path)); }
    unlink(path) {
      if (this.fs.lstatSync(this.host(path)).isDirectory()) { fail('EISDIR'); }
      this.fs.unlinkSync(this.host(path));
    }
    rename(from, to) { this.fs.renameSync(this.host(from), this.host(to)); }
    link(from, to) { this.fs.linkSync(this.host(from), this.host(to)); }
    symlink(target, path) { this.fs.symlinkSync(target, this.host(path)); }
    readlink(path) { return this.fs.readlinkSync(this.host(path)); }
    utimes(path, atim, mtim) {
      const s = this.fs.statSync(this.host(path), { bigint: true });
      const sec = (ns) => Number(ns) / 1e9;
      this.fs.utimesSync(this.host(path), sec(atim ?? s.atimeNs), sec(mtim ?? s.mtimeNs));
    }
  }

  const api = { createWasi, MemFs, NodeFs, ERRNO };
  if (typeof module !== 'undefined' && module.exports) { module.exports = api; }
  else { root.WasiFs = api; }
})(typeof globalThis !== 'undefined' ? globalThis : this);
