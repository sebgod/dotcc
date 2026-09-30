#!/usr/bin/env bash
# Run the interpreter build.sh built: run.sh [python args...], for example
#   run.sh -c "print('hello')"
#   run.sh script.py          (a path relative to the current directory)
#
# The standard library is cpython-src/Lib, reached as build/home/lib/python3.13
# (a junction on Windows, a symbolic link elsewhere, made on the first run).
# The interpreter is linked with -fposix-paths, so on Windows it sees POSIX
# paths: a PYTHONHOME of C:/... reads as /c/..., and a script may be named by
# a relative, a Windows or a /c/... path.
#
# The NativeAOT interpreter (BUILD_AOT=1 build.sh) is used when it is newer than
# the JIT build, so a later plain build.sh is not shadowed by a stale one.
#
# PY_SHARED=1 runs the shared build instead (BUILD_SHARED=1 build.sh): the thin
# interpreter over libpython, with its extension modules' lib-dynload directory
# on PYTHONPATH.
#
# Environment: CPYTHON_SRC, BUILD_OUT (default: build/), PY_SHARED.
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
pythonpath="${PYTHONPATH:-}"
if [ "${PY_SHARED:-0}" = 1 ]; then
  EXE="$OUT/shared/python/bin/Release/net10.0/python"
  [ -f "$EXE.exe" ] && EXE="$EXE.exe"
  [ -f "$EXE" ] || { echo "run.sh: no shared interpreter at $EXE (run BUILD_SHARED=1 build.sh)" >&2; exit 2; }
  # A Windows-form list separates with `;`, which -fposix-paths turns into the POSIX `:`.
  sep=":"; dynload="$(cd "$OUT/shared/lib-dynload" && pwd)"
  if windir="$(cd "$OUT/shared/lib-dynload" && pwd -W 2>/dev/null)"; then sep=";"; dynload="$windir"; fi
  pythonpath="$dynload${pythonpath:+$sep$pythonpath}"
fi

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
# MSYS would rewrite a /-rooted value into its own root for a native program.
PYTHONHOME="$home" PYTHONPATH="$pythonpath" MSYS2_ENV_CONV_EXCL="PYTHONHOME;PYTHONPATH" exec "$EXE" "$@"
