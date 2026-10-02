"""Pack CPython's standard library (the pinned tree's Lib/) into one file for the browser
sandbox, which unpacks it into its in-memory file system under the interpreter's home
(DotCC.Web/wwwroot/js/python-worker.js). Only the .py sources: the interpreter compiles what a
program imports, and keeps the bytecode for the next run in the same tab.

    python pack-stdlib.py LIB OUT

The pack is gzip over: the magic b"DOTCCPK1", the length of a JSON header as a 32-bit
little-endian integer, the header (a list of [path, size], each path relative to LIB with '/'
separators), then the files' bytes in that order. Left out: the test suites, and the modules
a sandbox cannot use (IDLE, Tk, turtle demos, ensurepip, lib2to3, pydoc's topics, venv).
"""
import gzip
import json
import os
import struct
import sys

SKIP = {'test', 'tests', 'idlelib', 'tkinter', 'turtledemo', 'ensurepip', 'lib2to3',
        'pydoc_data', 'venv', '__pycache__', 'site-packages'}


def main(lib, out):
    entries, blobs = [], []
    for here, dirs, files in os.walk(lib):
        dirs[:] = sorted(d for d in dirs if d not in SKIP and not d.startswith('test'))
        for name in sorted(files):
            if not name.endswith('.py'):
                continue
            path = os.path.join(here, name)
            rel = os.path.relpath(path, lib).replace(os.sep, '/')
            with open(path, 'rb') as f:
                data = f.read()
            entries.append([rel, len(data)])
            blobs.append(data)
    header = json.dumps(entries, separators=(',', ':')).encode('utf-8')
    raw = b'DOTCCPK1' + struct.pack('<I', len(header)) + header + b''.join(blobs)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, 'wb') as f:
        f.write(gzip.compress(raw, 9, mtime=0))
    print(f'pack-stdlib.py: {len(entries)} files, {len(raw) / 1e6:.1f} MB, '
          f'{os.path.getsize(out) / 1e6:.2f} MB gzip, in {out}')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit('usage: pack-stdlib.py LIB OUT')
    main(sys.argv[1], sys.argv[2])
