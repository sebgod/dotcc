#!/usr/bin/env bash
# Build CPython with dotcc's wat back end into build-wat/python.wasm: write a compilation
# database (compile_commands.json) of the interpreter's translation units, files.txt's core
# and boot units less the modules dotcc cannot compile yet (the set build.sh links), each
# with its own flags; compile them as one program with dotcc --target=wat
# --compile-commands; and assemble the module with wat2wasm. Run fetch.sh and generate.sh
# first. This is the interpreter the browser sandbox runs (GH #269).
#
# Environment: DOTCC_DLL (default: this checkout's Release build), CPYTHON_SRC (default:
# cpython-src/), BUILD_OUT (default: build-wat/), WAT2WASM (default: wat2wasm on PATH).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && (pwd -W 2>/dev/null || pwd))"
DLL="${DOTCC_DLL:-$HERE/../../DotCC/bin/Release/net10.0/dotcc.dll}"
SRC="${CPYTHON_SRC:-$HERE/cpython-src}"
OUT="${BUILD_OUT:-$HERE/build-wat}"
W2W="${WAT2WASM:-wat2wasm}"
CFG="$HERE/include"
[ -f "$DLL" ] || { echo "build-wat.sh: no dotcc build at $DLL" >&2; exit 2; }
[ -f "$SRC/Modules/config.c" ] || { echo "build-wat.sh: no Modules/config.c (run generate.sh)" >&2; exit 2; }
mkdir -p "$OUT"

# One entry per unit; the command keeps files.txt's shell quoting, which dotcc splits as a shell would.
json() { local s=${1//\\/\\\\}; printf '%s' "${s//\"/\\\"}"; }
db="$OUT/compile_commands.json"
{
  echo "["
  sep=""
  while IFS='|' read -r _ rel extra; do
    extra="${extra//@TREE@/$SRC}"
    cmd="dotcc -DNDEBUG -I$CFG -I$SRC/Include -I$SRC/Include/internal -I$SRC/Include/internal/mimalloc $extra -c $rel"
    printf '%s  {"directory": "%s", "file": "%s", "command": "%s"}' "$sep" "$(json "$SRC")" "$(json "$rel")" "$(json "$cmd")"
    sep=$',\n'
  done < <(grep -E '^(core|boot)\|' "$HERE/files.txt" | grep -v '|Modules/pwdmodule.c|')
  printf '\n]\n'
} > "$db"
echo "build-wat.sh: $(grep -c '"file"' "$db") units in $db"

start=$SECONDS
dotnet "$DLL" --target=wat --compile-commands "$db" -o "$OUT/python.wat"
echo "build-wat.sh: compiled in $((SECONDS - start)) s, $(wc -c < "$OUT/python.wat") bytes of wat"
start=$SECONDS
"$W2W" --enable-threads --enable-exceptions "$OUT/python.wat" -o "$OUT/python.wasm"
echo "build-wat.sh: assembled $OUT/python.wasm ($(wc -c < "$OUT/python.wasm") bytes) in $((SECONDS - start)) s"
