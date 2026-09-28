#!/usr/bin/env bash
# Build CPython with dotcc into build/python: compile the interpreter's
# translation units to object fragments (dotcc --emit=obj), link the objects
# into one C# program (dotcc --emit=csproj over the .cs objects) and build it
# with dotnet. Run fetch.sh and generate.sh first.
#
# The interpreter is files.txt's core and boot units, less the modules dotcc
# cannot compile yet (pwd: no <pwd.h>), built as CPython's release build is
# (-DNDEBUG). An object is recompiled when it is
# older than its source or than the dotcc build, so re-running after a dotcc
# fix redoes everything, and after a source edit only that unit.
#
# Environment: DOTCC_DLL, CPYTHON_SRC, BUILD_OUT (default: build/), BUILD_JOBS
# (default: the CPU count).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && (pwd -W 2>/dev/null || pwd))"
DLL="${DOTCC_DLL:-$HERE/../../DotCC/bin/Release/net10.0/dotcc.dll}"
SRC="${CPYTHON_SRC:-$HERE/cpython-src}"
OUT="${BUILD_OUT:-$HERE/build}"
JOBS="${BUILD_JOBS:-$(nproc)}"
[ -f "$DLL" ] || { echo "build.sh: no dotcc build at $DLL" >&2; exit 2; }
# The compiler proper is DotCC.Lib.dll beside the CLI; a fix there leaves dotcc.dll as it was.
# (Not named LIB: csc and the MSVC linker read LIB as their library search path.)
LIBDLL="$(dirname "$DLL")/DotCC.Lib.dll"
[ -f "$LIBDLL" ] || LIBDLL="$DLL"
[ -f "$SRC/Modules/config.c" ] || { echo "build.sh: no Modules/config.c (run generate.sh)" >&2; exit 2; }

mkdir -p "$OUT/obj"
export DLL LIBDLL SRC OBJ="$OUT/obj" CFG="$HERE/include"

# One unit, "<group>|<path>|<flags>", to $OBJ/<path with / as __>.cs.
unit() {
  local group rel extra name
  IFS='|' read -r group rel extra <<< "$1"
  name="${rel//\//__}"; name="${name%.c}"
  if [ -s "$OBJ/$name.cs" ] && [ "$OBJ/$name.cs" -nt "$SRC/$rel" ] && [ "$OBJ/$name.cs" -nt "$DLL" ] && [ "$OBJ/$name.cs" -nt "$LIBDLL" ]; then
    return 0
  fi
  extra="${extra//@TREE@/$SRC}"
  eval "set -- $extra"
  if ! dotnet "$DLL" --emit=obj -o "$OBJ/$name.cs" \
       -DNDEBUG -I"$CFG" -I"$SRC/Include" -I"$SRC/Include/internal" -I"$SRC/Include/internal/mimalloc" \
       "$@" "$SRC/$rel" > "$OBJ/$name.log" 2>&1; then
    rm -f "$OBJ/$name.cs"
    echo "build.sh: $rel failed:" >&2
    grep -m3 -E 'error|dotcc:' "$OBJ/$name.log" >&2 || tail -3 "$OBJ/$name.log" >&2
    return 1
  fi
}
export -f unit

grep -E '^(core|boot)\|' "$HERE/files.txt" | grep -v '|Modules/pwdmodule.c|' \
  | xargs -d '\n' -P "$JOBS" -I{} bash -c 'unit "$1"' _ {}
echo "build.sh: $(ls "$OBJ"/*.cs | wc -l) objects"

# Link: every object into one program (shared types deduplicated), then build it.
(cd "$OBJ" && dotnet "$DLL" --emit=csproj -o "$OUT/python" ./*.cs)
dotnet build "$OUT/python" -c Release --nologo -v quiet
echo "build.sh: built $OUT/python"
