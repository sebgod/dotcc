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
# fix redoes everything, and after a source edit only that unit. Only this
# build's units are linked, so other objects in the directory stay out.
#
# BUILD_AOT=1 also publishes a NativeAOT interpreter into build/python-aot
# (about two minutes more), which run.sh then prefers: it starts in under 0.1 s,
# where the JIT build compiles its startup path on every run (about 2 s).
#
# Environment: DOTCC_DLL, CPYTHON_SRC, BUILD_OUT (default: build/), BUILD_OBJ
# (default: $BUILD_OUT/obj; probe.sh's out/cs holds the same objects, built with
# the same flags, so CI links those instead of compiling again), BUILD_JOBS
# (default: the CPU count), BUILD_AOT.
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

OBJ="${BUILD_OBJ:-$OUT/obj}"
mkdir -p "$OUT" "$OBJ"
# Absolute, since the link runs from inside the object directory.
OUT="$(cd "$OUT" && (pwd -W 2>/dev/null || pwd))"
OBJ="$(cd "$OBJ" && (pwd -W 2>/dev/null || pwd))"
export DLL LIBDLL SRC OBJ CFG="$HERE/include"

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

UNITS="$(grep -E '^(core|boot)\|' "$HERE/files.txt" | grep -v '|Modules/pwdmodule.c|')"
xargs -d '\n' -P "$JOBS" -I{} bash -c 'unit "$1"' _ {} <<< "$UNITS"
objects=()
while IFS='|' read -r _ rel _; do
  name="${rel//\//__}"; objects+=("${name%.c}.cs")
done <<< "$UNITS"
echo "build.sh: ${#objects[@]} objects"

# Link: this build's objects into one program (shared types deduplicated), then build it.
(cd "$OBJ" && dotnet "$DLL" --emit=csproj -o "$OUT/python" "${objects[@]}")
dotnet build "$OUT/python" -c Release --nologo -v quiet
echo "build.sh: built $OUT/python"

if [ "${BUILD_AOT:-0}" = 1 ]; then
  # NativeAOT links with the platform toolchain; on Windows it finds MSVC through
  # vswhere, which is on PATH only inside a Developer prompt.
  vs_installer="/c/Program Files (x86)/Microsoft Visual Studio/Installer"
  if ! command -v vswhere >/dev/null 2>&1 && [ -d "$vs_installer" ]; then PATH="$vs_installer:$PATH"; fi
  dotnet publish "$OUT/python" -c Release --use-current-runtime -p:PublishAot=true \
    -o "$OUT/python-aot" --nologo -v quiet
  echo "build.sh: published $OUT/python-aot (NativeAOT)"
fi
