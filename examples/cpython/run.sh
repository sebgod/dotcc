#!/usr/bin/env bash
# Run the interpreter build.sh built: run.sh [python args...], for example
#   run.sh -c "print('hello')"
#   run.sh script.py          (a path relative to the current directory)
#
# The standard library is cpython-src/Lib, reached as build/home/lib/python3.13
# (a junction on Windows, a symbolic link elsewhere, made on the first run).
# CPython's POSIX build splits PYTHONHOME on ':' (prefix:exec_prefix), so on
# Windows the home is passed without its drive letter: a rooted path on the
# current drive, which .NET resolves there. For the same reason a script is
# best named by a relative path.
#
# The NativeAOT interpreter (BUILD_AOT=1 build.sh) is used when it is newer than
# the JIT build, so a later plain build.sh is not shadowed by a stale one.
#
# Environment: CPYTHON_SRC, BUILD_OUT (default: build/).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="${CPYTHON_SRC:-$HERE/cpython-src}"
OUT="${BUILD_OUT:-$HERE/build}"
EXE="$OUT/python/bin/Release/net10.0/python"
[ -f "$EXE.exe" ] && EXE="$EXE.exe"
[ -f "$EXE" ] || { echo "run.sh: no interpreter at $EXE (run build.sh)" >&2; exit 2; }
AOT="$OUT/python-aot/python"
[ -f "$AOT.exe" ] && AOT="$AOT.exe"
if [ -f "$AOT" ] && [ "$AOT" -nt "$EXE" ]; then EXE="$AOT"; fi

LIB="$OUT/home/lib/python3.13"
if [ ! -e "$LIB" ]; then
  mkdir -p "$OUT/home/lib"
  if command -v cygpath >/dev/null 2>&1; then
    cmd //c mklink //J "$(cygpath -w "$LIB")" "$(cygpath -w "$SRC/Lib")" >/dev/null
  else
    ln -s "$SRC/Lib" "$LIB"
  fi
fi

home="$(cd "$OUT/home" && (pwd -W 2>/dev/null || pwd))"
case "$home" in ?:/*) home="${home:2}" ;; esac
# MSYS would rewrite a /-rooted value into its own root for a native program.
PYTHONHOME="$home" MSYS2_ENV_CONV_EXCL=PYTHONHOME exec "$EXE" "$@"
