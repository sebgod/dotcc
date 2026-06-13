# Import mode — `-l<name>` / `-L<dir>` native library linking (dynamic + static)

## Context

dotcc can now *produce* native shared libraries (`-shared` → NativeAOT) and *consume* them explicitly via `<dlfcn.h>` (`dlopen`/`dlsym`, landed 2026-06-12). The missing piece is **implicit, linker-style consumption**: `dotcc main.c -lfoo -L/path` where the TU's extern prototypes resolve against a prebuilt native library with no `dlopen` in the source — the `-lfoo` feature C-SUPPORT.md line ~635 calls "designed, not built". This closes the chibi-plugin / cmake `target_link_libraries` gap. Static archives (`.a`/`.lib`) ride along where the platform allows (NativeAOT publish only — CoreCLR has no static-link step).

**Three load-bearing design decisions** (settled during exploration):

1. **Discriminator — line-band tracking (user-chosen over a name-harvest alternative).** Tokens carry no file origin (LALR.CC `SourcePosition` = line/col/offset) and every lexer buffer starts at line 1, so a synthetic-header proto (`printf`, runtime-provided via `using static Libc`) is indistinguishable from a user-header proto (import candidate) by position today. Fix it at the source, GCC-line-maps style: **LALR.CC's `BytesLexer` gains an optional `initialPosition`/line-base** (tiny, no behavior change for existing users), and `CPreprocessor.OnInclude` — which already creates a fresh sub-lexer per include (`CPreprocessor.cs:213`) and knows whether the name resolved to a synthetic `SystemHeaders` entry — lexes **synthetic-header bodies in a reserved line band** (base `1<<20`). `RegisterProto` then checks `SrcPos.Line >= SYNTHETIC_BASE` → the Symbol gets an exact `FromSystemHeader` flag. Declaration-exact, zero runtime cost, and the foundation for file-accurate diagnostics / `#line` later. Import candidate = **proto-only across ALL TUs** (one `IrBuilder` accumulates every TU — `Compiler.cs:296`) **AND referenced (called) AND not variadic AND not `FromSystemHeader`**. Two implementation cares: (a) verify `MacroExpander`'s position propagation (a proto generated at a user call site from a synthetic-header-defined macro must not inherit a band position — essentially nonexistent in practice, but check); (b) a render guard so no diagnostic ever prints a raw band line number (mask `Line % (1<<20)` and tag `<system header>` when in the band).

2. **Dynamic mechanism — GOT-style binding, NOT `[DllImport]` stubs.** With multiple `-l` libs, per-symbol library attribution is impossible for DllImport (one library name per stub; `SetDllImportResolver` resolves per-library, not per-symbol). Instead mirror ld.so: emit a `static unsafe class DotCcImports` of **function-pointer-typed static fields named exactly like the C functions** (`internal static delegate* unmanaged[Cdecl]<byte*, ulong> blob_size;`). C# invokes fn-ptr fields with call syntax and `using static DotCcImports` surfaces fields → **call sites need zero changes** (they already emit bare `name(args)`). A `__BindAll()` (called in `__DotCcEntry()` before `main`; static cctor in `-shared` mode) loads each `-l` lib via `NativeLibrary` (probing `-L` dirs with platform name variants, then system search) and binds each symbol from the **first lib that exports it** — linker search order, BIND_NOW-shaped, clean ld.so-style error on a missing symbol. Works under plain `dotnet run` AND NativeAOT. Reuses the just-landed `CType.Func.IsNativeCallConv` → `delegate* unmanaged[Cdecl]<…>` rendering (`CSharpTarget.cs`).

3. **Static mechanism — `[DllImport]` + `<DirectPInvoke>`/`<NativeLibrary>`, NativeAOT-publish-only.** A `.a`/`.lib` passed as a direct input (clang accepts that) → classic `[DllImport("<name>", ExactSpelling=true, CallingConvention=Cdecl)]` extern stubs in `DotCcStaticImports` + generated-csproj `<ItemGroup>` with `<DirectPInvoke Include="<name>"/>` + `<NativeLibrary Include="<abs path>"/>` + `<PublishAot>true</PublishAot>`. ILC resolves the stubs at native link. `dotnet run` → DllNotFoundException (inherent; dotcc warns "static linking requires `dotnet publish -r <RID>`"). V1: mixing static archives + dynamic `-l` in one compile → clear `CompileException` (symbol attribution across the boundary is unknowable without reading archives).

**V1 scope cuts (loud, documented, not silent):** direct *calls* only — address-of / fn-ptr-store of an imported function ⇒ Warning (the import set is only known after all TUs, so `IsNativeCallConv` can't propagate during IR build); variadic candidates skipped with Warning + emitted comment (same as `-shared` export skip); extern *data* imports (`extern int foo_verbosity;`) ⇒ Warning, unsupported; no `-Wl,` passthrough; an import-candidate name colliding with a C global ⇒ Warning + skip (the definition wins, C-shaped). No warning for runtime-provided names — libc calls resolving to the managed runtime while `-l` is present is the normal case (clang doesn't warn either).

## Implementation steps (ordered; each builds green)

### 0. LALR.CC: `BytesLexer` initial position — `../../sharpastro/LALR.CC`
Add an optional `SourcePosition initialPosition = default` (or `int lineBase = 1`) to `BytesLexer` (+ the `FromString`/`FromBytes` factories; mirror on `PipeBytesLexer` if trivial). Default behavior unchanged — existing-user-safe. LALR.CC unit test: a lexer started at line base N yields tokens with `Line >= N`. **Do this first and tag the release early** (per the established flow: tag `vX.Y.Z` publishes to NuGet; dotcc CI builds the NuGet path, so dotcc's CI can only go green after the tag — sibling mode covers local dev immediately).

### 1. `ImportOptions` + signature threading — `DotCC.Lib/Compiler.cs`
`public sealed record ImportOptions(IReadOnlyList<string> LinkLibraries, IReadOnlyList<string> LibraryDirs, IReadOnlyList<string> StaticArchives)` with `Empty` + `HasAny`. Add `ImportOptions? imports = null` (trailing optional, no breakage) to `EmitCSharp`, `EmitObject`, `LinkObjects`, `BuildGeneratedCsproj`. No behavior change yet.

### 2. Synthetic line band — `CPreprocessor.cs` + `SymbolTable.cs`/`IrBuilder.cs`
- A shared constant `SyntheticLineBase = 1 << 20` (home: `Compiler` or `SrcPos`).
- `CPreprocessor.OnInclude`: when the include resolved to a synthetic `SystemHeaders` entry, create the sub-lexer with the band base (`CPreprocessor.cs:213`). Synthetic headers including synthetic siblings recurse through the same path — band preserved. User headers stay at line 1 (unchanged).
- `Symbol` gains `public bool FromSystemHeader { get; init; }`; `RegisterProto`/`DeclareFunc` set it from `SrcPos.From(fnSig).Line >= SyntheticLineBase`.
- **Render guard**: wherever a `pos.Line` reaches a user-facing message (DialectGate, Diagnostic, CompileException), mask the band (`line >= Base ? $"<system header>:{line - Base}" : line`) — a tiny shared helper. Grep for `.Line` consumers and route them through it.
- **Verify `MacroExpander` position propagation**: expansion tokens at a user call site must carry call-site (non-band) positions; add a unit test pinning that (a synthetic-header macro used in the TU → resulting symbols not `FromSystemHeader`).
- Unit tests: `printf` proto → `FromSystemHeader == true`; a user-header proto via `-I` → `false`; same-name proto in TU → `false`.

### 3. Import-candidate tracking — `IrBuilder.cs`
- `_protoOnlyFuncs` (name→Symbol): added in `RegisterProto` when no `_fnDefSites` entry; removed in `BuildFuncDef`.
- `_referencedFuncs`: added in `BuildCall` when the callee name resolves (conservative superset is fine — filtered later). Indirect calls: nothing.
- `public IReadOnlyDictionary<string, Symbol> ProtoOnlyReferenced` — proto-only ∧ referenced ∧ non-variadic ∧ **not `FromSystemHeader`**.
- `public IReadOnlyList<string> ExternDataReferenced` — `SymKind.Var` + `Storage.Extern`, referenced, no definition (for the V1 warning).

### 4. Runtime loader helper — `DotCC.Libc/NativeImports.cs` (new)
`public static class NativeImports` (NOT inside `Libc`; the splice handles sibling types like `Float128` already — **emitted references must be namespace-free**, the splice strips namespaces). Method-level unsafe. `LoadLibrary(string name, string[] searchDirs)`: probe each `-L` dir with platform variants (`lib<name>.so`/`<name>.so`; `<name>.dll`/`lib<name>.dll`; `lib<name>.dylib`/`<name>.dylib` — no `.so.6` guessing), then variants via system search, then plain `TryLoad(name)`. `TryResolveExport(IntPtr[] handles, string symbol, out void* fn)`: first-lib-wins. Picked up by the existing `DotCC.Libc/*.cs` embed glob — no csproj change. Always-on unit test: `LoadLibrary("c")` on Linux / `LoadLibrary("kernel32")` on Windows → non-zero.

### 5. `DotCcImports` emission — `Compiler.cs` (`EmitCSharp` + `BuildShell` / `BuildLibraryShell`)
- After `BuildIr`: `ComputeImportCandidates(irBuilder, imports)` — `ProtoOnlyReferenced` (already excludes `FromSystemHeader`) minus global-name collisions (Warning + skip); Warnings for variadic / extern-data per the scope cuts.
- `BuildShell`: when candidates exist, emit `static unsafe class DotCcImports` — one field per candidate, type rendered via `_target.RenderType(func with { IsNativeCallConv = true })` (do NOT mutate the Symbol — marker stays local to the field decl); `internal static void __BindAll()` loading `imports.LinkLibraries` in order via `NativeImports.LoadLibrary(name, new[]{ -L dirs })`, binding each symbol via `TryResolveExport`, `throw new DllNotFoundException("dotcc: symbol 'x' not found in: a, b")` on miss. Call `DotCcImports.__BindAll();` at the top of `__DotCcEntry()` (deterministic, on the 64MB-stack thread). Add `using static DotCcImports;`.
- `BuildLibraryShell` (`-shared` + `-l`): same class but bind in a **static cctor** (no entry point in a lib; first touch of any import field triggers it).
- `--target=wat` + `-l` → warn ignored.
- Unit test: emitted text has the field + `__BindAll` + Roslyn-compiles clean (no CS0103); without `-l` → unchanged CS0103 baseline.

### 6. Separate compilation — fragment markers + `LinkObjects` resolution
- New marker `//!!dotcc-obj import:<name> <cs-type-spelling>` (split on first space; the `delegate* unmanaged[Cdecl]<…>` spelling has no space before `<`). `EmitObject` serializes ALL `ProtoOnlyReferenced` candidates (system-header protos already excluded by the band flag; no `-l` known at obj time — the linker decides).
- `LinkObjects`: parse markers; a name defined in any fragment → drop; survivors + `imports.HasAny` → emit `DotCcImports` exactly as step 5; survivors without `-l` → nothing (today's CS0103).
- `examples/cmake-demo/dotcc-toolchain.cmake`: append `<LINK_LIBRARIES>` to `CMAKE_C_LINK_EXECUTABLE`; `dotcc-link.sh` already forwards `"$@"`.

### 7. Static archives — csproj + stubs
- `Program.cs` input partition: third bucket for `.a`/`.lib` extensions → `ImportOptions.StaticArchives` (absolute-pathed).
- Static + dynamic mixed → `CompileException("mixing static archives and -l dynamic libraries is not yet supported")`.
- Candidates → `static unsafe class DotCcStaticImports` of `[DllImport("<libname>", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)] internal static extern …;` stubs (lib name = archive filename minus `lib`/extension; with multiple archives the native linker resolves symbols across all of them regardless of which stub name they carry). `using static DotCcStaticImports;`.
- `BuildGeneratedCsproj`: `<PublishAot>true</PublishAot>` + `<ItemGroup>` with `<DirectPInvoke>` per archive name + `<NativeLibrary>` per abs path. stderr note: "static native archives require `dotnet publish -c Release -r <RID>`".
- Classic `[DllImport]` (not `[LibraryImport]`) — no source-gen, survives the generator-less Roslyn compile in tests, matches `DotCC.Libc`'s own documented pattern.

### 8. CLI — `DotCC/Program.cs`
`Option<string[]>` for `-l` and `-L` (space-separated form); harvest glued `-lfoo`/`-L/dir` in the existing unmatched-token loop **before** the ignore-warning (tokens of length > 2 with those prefixes). Build `ImportOptions`, thread through `Run` → `EmitCSharp`/`LinkObjects`/`BuildGeneratedCsproj`.

### 9. Unit tests — `DotCC.Tests/ImportModeTests.cs` (new)
The list: no-`-l` baseline unchanged (CS0103); `-l` → `delegate* unmanaged[Cdecl]` field emitted + Roslyn-compiles; system-header proto (`printf` via `<stdio.h>`) never imported (band flag); user-header proto via `-I` IS imported; variadic candidate → Warning + no stub; extern data → Warning; global-name collision → Warning + skip; obj fragment carries `import:` marker; `LinkObjects` def-wins; `LinkObjects` + `-l` emits `DotCcImports`; static csproj has `<DirectPInvoke>`/`<NativeLibrary>`/`<PublishAot>`; mixed static+dynamic throws; band render-guard (no raw 1048601 line in any diagnostic); macro-expansion position pin (synthetic-header macro used in TU → non-band).

### 10. Oracle tests — `DotCC.FunctionalTests` (opt-in, same `DOTCC_RUN_SHARED_LIB_ORACLE=1` gate)
Reuse `SharedLibOracle.RunCapture` (script-file transport). Three legs in a new `NativeImportOracleTests` class:
- **A — gcc-built `.so`**: `gcc -shared -fPIC` a two-export lib in the workdir → dotcc-emit a consumer with `ImportOptions(["mylib"], [workdir], [])` → `dotnet run` → assert stdout.
- **B — dotcc-consumes-dotcc via `-l`**: publish the NativeAOT lib (existing flow) → consumer with `-ldotccsharedlib -L<publishdir>` (no `lib` prefix on NativeAOT output — the `<name>.so` variant probe covers it) → output identical to the dlopen leg.
- **C — static archive**: `gcc -c` + `ar rcs libmylib.a` → dotcc consumer with the `.a` as input → `dotnet publish -r <rid>` the consumer → run the native exe → assert stdout.
- CI: extend the `shared-lib-oracle` job filter to `…~SharedLibOracleTests|…~NativeImportOracleTests` in `dotnet.yml` (the `~` is substring-match; the new class name doesn't contain the old).

### 11. Docs + memory
- `C-SUPPORT.md`: `-lfoo` row ❌ → ✅/🟡 (GOT-style dynamic + DirectPInvoke static, the V1 cuts, when-to-use vs `dlopen`); dlfcn row cross-reference; libc at-a-glance row.
- `README.md` CLI table + the linking story; `CLAUDE.md` CLI surface table; `examples/cmake-demo/README.md` (`target_link_libraries` now flows); `examples/chibi/README.md` pointer (import mode exists; the shared-heap plugin problem remains separate).
- Memory: new `project_import_mode.md` (GOT-style rationale, line-band discriminator, static-needs-publish, V1 cuts) + MEMORY.md line; update `native_fn_ptr_marker.md` cross-link.

## Verification

| Layer | Proves | Runs |
|---|---|---|
| `ImportModeTests` (unit) | candidate computation, emission shape, baseline preserved, obj/link markers, csproj items, error/warning paths | always, all CI jobs |
| `NativeImports` unit test | loader probes real system libs on both OSes | always |
| Oracle leg A | GOT binding calls a real gcc-built `.so` correctly | opt-in, WSL + `shared-lib-oracle` CI |
| Oracle leg B | dotcc-consumes-dotcc via `-l` — output identical to the dlopen leg | opt-in, same |
| Oracle leg C | static `.a` through DirectPInvoke + NativeAOT publish | opt-in, same |
| cmake-demo | `<LINK_LIBRARIES>` threading doesn't regress the existing demo | WSL (Ninja) |

Local: full unit + functional on Windows; oracle legs in WSL; cmake-demo in WSL. Regression watch: all existing fixtures byte-identical when no `-l` given (candidates only computed when `imports.HasAny`); Lua + chibi workflows green. Push → full CI matrix.

## Honest limits (documented, not silent)
- Calls only: `&imported_fn` / storing into fn-ptrs ⇒ Warning (future: post-IR fixup pass marking consumers `IsNativeCallConv`).
- Variadic and extern-data imports ⇒ Warning, skipped.
- Static archives need `dotnet publish -r <RID>`; mixed static+dynamic rejected.
- `-l` name probing follows platform conventions (`libfoo.so` / `foo.dll`); cross-OS naming mismatches (zlib1.dll) are the user's to align — same as any cross-platform C build.
- A native lib interposing a libc-named symbol (link-order interposition) is not modeled — a proto declared in a dotcc synthetic header always resolves managed. (A user re-declaring the function themselves *without* including the system header IS importable — declaration-exact, the line-band payoff.)
- dotcc's CI (NuGet path) needs the LALR.CC `initialPosition` release tagged before the dotcc push can go green; sibling mode covers local dev.
