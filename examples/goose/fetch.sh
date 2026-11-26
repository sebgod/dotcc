#!/usr/bin/env bash
# Download the pinned Goose release for this host into build/ (gitignored) and
# check it against the release's sha256. Unlike Lua and chibi-scheme nothing is
# vendored: the input dotcc compiles is the C the Goose compiler generates, so
# the compiler itself is the pin. Prints the extracted directory, which run.sh
# also finds through build/goose-dir.
set -euo pipefail

GOOSE_VERSION="${GOOSE_VERSION:-v2026.4}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD="$HERE/build"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)
    ASSET="goose-$GOOSE_VERSION-linux-x64.tar.gz"
    SHA256=559ad7572df89fcd1273512d8679528bcdf876c252ec1f31608d3808b24125b3 ;;
  Darwin-arm64)
    ASSET="goose-$GOOSE_VERSION-macos-arm64.tar.gz"
    SHA256=8ef0b5a864a719bd095b8376bed069d0edc77a0ce8f3cb9d59fdf87291a3a922 ;;
  Darwin-x86_64)
    ASSET="goose-$GOOSE_VERSION-macos-x64.tar.gz"
    SHA256=471beefd5787ff96b68c1a059ac4de0973a86a540efda9f2b084b540b5d73db0 ;;
  MINGW*-x86_64|MSYS*-x86_64|CYGWIN*-x86_64)
    ASSET="goose-$GOOSE_VERSION-windows-x64.zip"
    SHA256=87ceb0ff2394e0ac4154b6c6b865c084cadbf236558cc956b114f37d6e619cdb ;;
  *)
    echo "fetch.sh: no Goose $GOOSE_VERSION release for $(uname -s)-$(uname -m)" >&2
    exit 1 ;;
esac
# The digests above are v2026.4's; another version needs its own.
if [ "$GOOSE_VERSION" != v2026.4 ]; then
  echo "fetch.sh: only v2026.4's sha256 digests are pinned here" >&2
  exit 1
fi

mkdir -p "$BUILD"
ARCHIVE="$BUILD/$ASSET"
if [ ! -f "$ARCHIVE" ]; then
  curl -fsSL -o "$ARCHIVE.part" "https://github.com/aardappel/goose/releases/download/$GOOSE_VERSION/$ASSET"
  mv "$ARCHIVE.part" "$ARCHIVE"
fi
echo "$SHA256  $ARCHIVE" | sha256sum -c - >&2

DEST="$BUILD/goose-$GOOSE_VERSION"
if [ ! -d "$DEST" ]; then
  mkdir -p "$DEST.part"
  case "$ASSET" in
    *.zip) unzip -q "$ARCHIVE" -d "$DEST.part" ;;
    *) tar -xzf "$ARCHIVE" -C "$DEST.part" ;;
  esac
  mv "$DEST.part" "$DEST"
fi
# The archive holds one directory with the compiler, its stdlib and the samples.
GOOSE_DIR="$(dirname "$(find "$DEST" -maxdepth 3 -type d -name samples | head -1)")"
[ -d "$GOOSE_DIR/samples" ] || { echo "fetch.sh: no samples/ in $DEST" >&2; exit 1; }
echo "$GOOSE_DIR" > "$BUILD/goose-dir"
echo "$GOOSE_DIR"
