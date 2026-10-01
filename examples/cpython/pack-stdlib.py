"""Pack the pinned tree's Lib/ into one zip for the web sandbox's Python page.

The browser has no file system of its own: the page downloads this zip once and
unpacks it into the tab's in-memory one, as <home>/lib/python3.13, before it
starts the interpreter. Only the pure-Python standard library goes in; the test
suite, the GUI and packaging tools and the byte-code caches stay out, since
nothing in a browser tab can use them.

    python examples/cpython/pack-stdlib.py [cpython-src/Lib] [out.zip]
"""

import os
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
LIB = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "cpython-src", "Lib")
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "build", "stdlib.zip")

# Top-level directories under Lib/ that a tab has no use for.
SKIP_DIRS = {
    "test", "idlelib", "tkinter", "turtledemo", "ensurepip", "venv", "lib2to3",
    "pydoc_data", "__pycache__", "site-packages",
}
SKIP_FILES = {"turtle.py", "pydoc.py"}

count = 0
os.makedirs(os.path.dirname(OUT) or ".", exist_ok=True)
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(LIB):
        rel = os.path.relpath(root, LIB)
        dirs[:] = sorted(d for d in dirs if d != "__pycache__" and not (rel == "." and d in SKIP_DIRS))
        for name in sorted(files):
            if not name.endswith(".py") or (rel == "." and name in SKIP_FILES):
                continue
            path = os.path.join(root, name)
            arc = os.path.normpath(os.path.join(rel, name)).replace(os.sep, "/")
            # A fixed time keeps the zip byte-identical from one build to the next.
            info = zipfile.ZipInfo(arc, date_time=(2024, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as f:
                z.writestr(info, f.read())
            count += 1

print(f"pack-stdlib: {count} modules, {os.path.getsize(OUT) / 1024 / 1024:.2f} MB -> {OUT}")
