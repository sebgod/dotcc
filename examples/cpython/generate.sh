#!/usr/bin/env bash
# Generate the sources CPython's own build generates, into cpython-src/:
#
# - Python/frozen_modules/*.h: the frozen modules (importlib's bootstrap, the
#   startup stdlib, getpath), compiled and marshalled by
#   Programs/_freeze_module.py, CPython's pure-Python twin of the
#   _freeze_module program. It needs a host Python 3.13: bytecode is stable
#   within a minor version, so 3.13.x's frozen code loads in the pinned 3.13.
# - Modules/config.c: the table of built-in modules (makesetup's job), from
#   Modules/config.c.in and the Setup.bootstrap modules dotcc compiles.
#
# Run after fetch.sh. PYTHON names the host interpreter (default: python).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && (pwd -W 2>/dev/null || pwd))"
SRC="${CPYTHON_SRC:-$HERE/cpython-src}"
PY="${PYTHON:-python}"
[ -d "$SRC/Lib" ] || { echo "generate.sh: no CPython sources at $SRC (run fetch.sh)" >&2; exit 2; }
"$PY" -c 'import sys; sys.exit(sys.version_info[:2] != (3, 13))' \
  || { echo "generate.sh: $PY is $("$PY" -V 2>&1), frozen modules need Python 3.13" >&2; exit 2; }

# The frozen modules Python/frozen.c includes, and getpath.c's getpath.
out="$SRC/Python/frozen_modules"
mkdir -p "$out"
while read -r name path; do
  "$PY" "$SRC/Programs/_freeze_module.py" "$name" "$SRC/$path" "$out/$name.h"
done <<'EOF'
importlib._bootstrap Lib/importlib/_bootstrap.py
importlib._bootstrap_external Lib/importlib/_bootstrap_external.py
zipimport Lib/zipimport.py
abc Lib/abc.py
codecs Lib/codecs.py
io Lib/io.py
_collections_abc Lib/_collections_abc.py
_sitebuiltins Lib/_sitebuiltins.py
genericpath Lib/genericpath.py
ntpath Lib/ntpath.py
posixpath Lib/posixpath.py
os Lib/os.py
site Lib/site.py
stat Lib/stat.py
importlib.util Lib/importlib/util.py
importlib.machinery Lib/importlib/machinery.py
runpy Lib/runpy.py
__hello__ Lib/__hello__.py
__phello__ Lib/__phello__/__init__.py
__phello__.ham Lib/__phello__/ham/__init__.py
__phello__.ham.eggs Lib/__phello__/ham/eggs.py
__phello__.spam Lib/__phello__/spam.py
frozen_only Tools/freeze/flag.py
getpath Modules/getpath.py
EOF
echo "frozen modules: $(ls "$out" | wc -l) headers in Python/frozen_modules"

# Modules/config.c: Setup.bootstrap's modules, less pwd (dotcc has no <pwd.h>).
modules="atexit faulthandler posix _signal _tracemalloc _suggestions _codecs _collections errno _io
  itertools _sre _sysconfig _thread time _typing _weakref _abc _functools _locale _operator _stat _symtable"
"$PY" - "$SRC/Modules/config.c.in" "$SRC/Modules/config.c" $modules <<'PY'
import sys
src, dst, mods = sys.argv[1], sys.argv[2], sys.argv[3:]
text = open(src, encoding="utf-8").read()
m1, m2 = "/* -- ADDMODULE MARKER 1 -- */", "/* -- ADDMODULE MARKER 2 -- */"
assert text.count(m1) == 1 and text.count(m2) == 1
decls = "".join(f"extern PyObject* PyInit_{m}(void);\n" for m in mods)
entries = "".join(f'    {{"{m}", PyInit_{m}}},\n' for m in mods)
text = text.replace(m1, decls + m1).replace(m2, entries + m2)
open(dst, "w", encoding="utf-8", newline="\n").write(text)
print(f"Modules/config.c: {len(mods)} built-in modules")
PY
