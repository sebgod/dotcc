#!/bin/bash
# dotcc link helper for the CMake toolchain.
# Usage: dotcc-link.sh <dotcc.dll> <target> <objects...>
#
# Links the `.cs` object fragments into a .NET program (dotcc merges + builds),
# then writes a launcher at <target> that runs it — so CMake's "executable" is
# something ctest / the shell can run directly.
set -e
DLL="$1"; TARGET="$2"; shift 2

GEN="${TARGET}.dotcc-gen"
rm -rf "$GEN"
# `.cs` inputs → dotcc links them; --emit=build produces the assembly.
dotnet "$DLL" "$@" --emit=build -o "$GEN" 1>&2

# Embed an ABSOLUTE dll path so the launcher works from any cwd (ctest runs it
# from the build dir, a shell may run it from elsewhere — a real linked exe
# isn't cwd-dependent, so neither is ours).
APP_DLL="$(realpath "$GEN/bin/Release/net10.0/dotcc-out.dll")"
printf '#!/bin/bash\nexec dotnet "%s" "$@"\n' "$APP_DLL" > "$TARGET"
chmod +x "$TARGET"
