# Testing

> Extracted from CLAUDE.md (2026-07-07) — the full testing reference (suite anatomy +
> every opt-in oracle). CLAUDE.md keeps the essentials and points here.

Two projects, both xUnit v3 on Microsoft.Testing.Platform.

**Unit tests (`DotCC.Tests/`).** Drive `Compiler.EmitCSharp` / `Preprocess` against inline C strings, assert on the returned string (e.g. emitted C# contains `static unsafe int main`; `--emit=csproj` omits the `#:property` header; parse errors / missing `main` raise `CompileException`; `#define` substitutes). No fixture files — write a temp `.c`, call, assert, delete. Fast.

**Functional tests (`DotCC.FunctionalTests/`).** Fixture-driven. For each `Fixtures/<name>/` with `.c` file(s) + an `expected-stdout.txt` sidecar: `EmitCSharp` → `CSharpCompilation.Create(OutputKind.ConsoleApplication, allowUnsafe: true)` → fresh `AssemblyLoadContext.LoadFromStream` → reflect entry point → invoke with `Console.Out` redirected → captured stdout `ShouldBe` the sidecar (normalized for line-ending / trailing-newline noise). **Adding a fixture is just dropping a folder** — `FixtureRunner.Discover()` walks `Fixtures/` at runtime and `[Theory]/[MemberData]` materialises one case per directory. Same shape as compiler test suites generally: golden C in, captured stdout vs a sidecar. (Fixture assemblies load into a **non-collectible** `AssemblyLoadContext` on purpose — emitted programs take addresses of file-scope statics via `Unsafe.AsPointer(ref static)`, which is only sound in non-moving storage; a collectible ALC stores non-GC statics movably and a compacting GC can relocate one mid-expression — a real, fixed flake. Don't revert.)

## Differential oracles (opt-in, snapshot model)

The committed `expected-stdout.txt` IS the cached compiler output; `FixtureTests` validates dotcc against it in-process every run. Two oracles re-check that snapshot against a *real* compiler, off by default for speed:
- **MSVC** (`MsvcOracleTests.cs`, `DOTCC_RUN_MSVC_ORACLE=1`): cl.exe per fixture; asserts MSVC == dotcc emit == snapshot. `DOTCC_REGEN_BASELINE=1` rewrites the snapshot in-place from MSVC (review + commit the diff).
- **gcc-in-WSL** (`GccWslOracleTests.cs`, `DOTCC_RUN_GCC_ORACLE=1`): a second reference compiler covering ground MSVC can't (e.g. C23 bare `bool`, which MSVC rejects). `DOTCC_REGEN_BASELINE=1` here refreshes from gcc — but requires the gcc run flag too, so a plain regen doesn't silently hand baseline authority to gcc.

A **third** oracle is a different shape — not a per-fixture snapshot check, but a native round-trip:
- **Native shared-library round-trip** (`SharedLibOracleTests.cs`, `DOTCC_RUN_SHARED_LIB_ORACLE=1`): `-shared`-compiles a C lib, NativeAOT-publishes it to a real `.so`, then links + runs a hand-written C consumer that calls the `[UnmanagedCallersOnly]` cdecl exports through the plain C ABI. Proves the export ABI end-to-end — `LibraryModeTests` only checks the managed export metadata (it skips the publish). Needs `clang`/`zlib` (the NativeAOT linker) + `gcc` (the consumer); its own ubuntu CI job.

The **Zig front-end** has its own differential oracle:
- **zig** (`ZigOracleTests.cs`, `DOTCC_RUN_ZIG_ORACLE=1`): builds each Zig oracle program with the real `zig` compiler (CI pins the newest DURABLE tagged release, **`0.16.0`** — dev/master tarballs are GC'd off ziglang.org's download index within days, so a dev pin would rot; locally, whatever `zig` is on PATH, which for this campaign is `0.17.0-dev.667+0569f1f6a`, the version whose std source dotcc compiles) via `zig build-exe`, runs the binary, and asserts dotcc's exit code + stdout match — stderr too, but only on a success exit (an error-exit's real-zig stack trace is deliberately not reproduced). zig is a single self-contained binary on every host, so no WSL hop. On the win-arm64 dev box zig lives at `~/AppData/Local/Programs/Zig` — `export PATH="$PATH:/c/Users/<user>/AppData/Local/Programs/Zig"` + the env var runs the differential locally. **Oracle programs must be valid under BOTH pinned versions** — 0.16.0 in CI and the local 0.17-dev — so a surface where the two genuinely differ (notably `@typeInfo`'s member lists: 0.17-dev's parallel `field_names`/`field_types`/`field_values` replaced 0.16's `fields: []const StructField`) cannot have an oracle program at all, and is covered by emit pins plus a by-hand local run instead. Oracle programs must also be valid under real zig's stricter rules (unused capture, shadowing, unreachable code) — dotcc is deliberately more lenient, and an invalid oracle program fails in CI, not locally, if you skip the local run.

All oracles skip cleanly when the host toolchain isn't present. A fixture opts out of a snapshot oracle with a `no-msvc-oracle.txt` / `no-gcc-oracle.txt` sidecar (contents = skip reason) — used for ABI-divergent cases (`float128-basic` has no MSVC `_Float128`; `float-limits`' `LDBL_DIG` depends on `long double` width). **`Process.Start` is confined to these oracle modes** (cl.exe / `wsl.exe` / `zig` + the program + a one-shot vcvars capture; the shared-lib oracle adds `dotnet publish` + `gcc`) — never on the always-on path.

## Run discipline

- Build once, then run suites **serially** (`--no-build` after a full build) — never two test processes in parallel (a parallel restore/build deadlocks arm64 MSBuild).
- After any `.lalr.yaml` grammar edit, a FULL build of the consuming project before testing — `--no-build` runs a stale generated parse table and makes a correct grammar change look broken.
- A test that fails then passes on re-run is a bug to root-cause (usually shared state), never to dismiss.
- CI: GitHub Actions — `ci` (matrix incl. the oracle legs), `lua`, `chibi`. Verify per-JOB (`gh run view <id>`) before merging.
