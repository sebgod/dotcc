# WF0 surface-probe modules (fable-wasm.md)

Committed `.wasm` blobs for the wasm-frontend campaign's WF0 surface probe
(`WasmSurfaceProbeTests`, opt-in via `DOTCC_RUN_WASM_PROBE=1`) — one module per
real producer, probed alongside dotcc's own wat-backend corpus. The blobs are
committed so the probe (and later reader/lifter tests) runs with **no toolchain
on the host**; a toolchain is only needed to *regenerate* them (the
sidecar-is-the-reference pattern). Sources sit alongside their blobs.

Every command below was run and verified on this repo's dev machine
(win-arm64, 2026-07-09). Paths use the local install locations; adjust to taste.

## `geom-embedded-swift.wasm` ← `geom.swift` — Embedded Swift (the campaign's end-goal producer)

Compiled with the **host Windows swiftc 6.3.1** (`swift-6.3.1-RELEASE`,
aarch64-unknown-windows-msvc) plus the official swift.org **wasm Swift SDK
bundle** — no separate toolchain install. Three non-obvious steps, found
empirically (this was the campaign's "biggest logistics unknown"; it works):

1. **Fetch + extract the SDK bundle** (~72 MB; contains the wasm32 target stdlib,
   the embedded stdlib variant, and a WASI sysroot — the host toolchain ships
   none of these):

   ```bash
   curl -sLO https://download.swift.org/swift-6.3.1-release/wasm-sdk/swift-6.3.1-RELEASE/swift-6.3.1-RELEASE_wasm.artifactbundle.tar.gz
   tar -xzf swift-6.3.1-RELEASE_wasm.artifactbundle.tar.gz
   SDKROOT=swift-6.3.1-RELEASE_wasm.artifactbundle/swift-6.3.1-RELEASE_wasm/wasm32-unknown-wasip1
   ```

2. **Mirror compiler-rt where the host clang looks.** The link step (clang 21
   inside the Swift toolchain) hardwires
   `<its resource dir>/lib/wasm32-unknown-wasip1/libclang_rt.builtins.a`, which
   the Windows toolchain doesn't ship; the bundle carries the archive under the
   *old* layout name. Mirror it into a scratch resource dir and point only the
   link step there:

   ```bash
   mkdir -p fake-resdir/lib/wasm32-unknown-wasip1
   cp "$SDKROOT/swift.xctoolchain/usr/lib/clang/lib/wasip1/libclang_rt.builtins-wasm32.a" \
      fake-resdir/lib/wasm32-unknown-wasip1/libclang_rt.builtins.a
   ```

3. **Compile** (embedded triple is `wasm32-unknown-wasip1`, per the bundle's
   `embedded-swift-sdk.json`; flags mirror its toolset file):

   ```bash
   swiftc -target wasm32-unknown-wasip1 \
     -enable-experimental-feature Embedded -wmo -static-stdlib -parse-as-library \
     -resource-dir "$SDKROOT/swift.xctoolchain/usr/lib/swift" \
     -sdk "$SDKROOT/WASI.sdk" \
     -Xclang-linker -resource-dir="$(cygpath -m "$PWD/fake-resdir")" \
     -Xlinker --no-entry \
     geom.swift -o geom-embedded-swift.wasm
   ```

The module exports exactly the `@_expose(wasm, …)` names (`square`,
`vec_length`, `dot`, `imax`, `fib`) — no demangling needed for the public API.

## `square-clang.wasm` ← `square.c` — clang `--target=wasm32`

The clang bundled with the Swift 6.3.1 toolchain (clang 21.1.6; any clang with
`wasm-ld` works):

```bash
clang --target=wasm32 -O2 -nostdlib -Wl,--no-entry -Wl,--export-all \
  -o square-clang.wasm square.c
```

## `square-zig.wasm` ← `square.zig` — zig 0.17.0-dev.667

The same zig install the zig oracle pins:

```bash
zig build-lib -target wasm32-freestanding -O ReleaseSmall -dynamic square.zig
mv square.wasm square-zig.wasm
```

## Loud cut: no Rust module

The plan's corpus row (c) also wanted a Rust `no_std` function; this machine has
no `rustc` and the probe corpus shouldn't gate on installing one. Add
`square-rust.wasm` (`rustc --target wasm32-unknown-unknown -C opt-level=2` over a
`#![no_std]` `#[no_mangle]` fn) when a Rust toolchain is around; the probe test
picks up any `*.wasm` dropped in this directory automatically.
