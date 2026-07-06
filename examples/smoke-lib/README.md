# smoke-lib — a dotcc `-shared` native library, called from real C

`math.c` is a tiny C library: two plain exports (`add`, `scale`), one that calls
an internal `static` helper (`double_it`), and the `static` helper itself
(`helper`, *not* exported). It demonstrates dotcc's `-shared` mode — compiling C
into a **C-callable native shared library** through .NET NativeAOT.

## Build the native library

```bash
dotcc -shared math.c -o build/          # emits build/Program.cs + build/build.csproj
cd build && dotnet publish -c Release -r <RID>
```

`<RID>` is your runtime, e.g. `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`.
The publish produces a real shared library under
`build/bin/Release/net10.0/<RID>/publish/`:

| OS | File | Notes |
|---|---|---|
| Linux | `<name>.so` | **No `lib` prefix** (NativeAOT names it `<AssemblyName>.so`) — link by full path, not `-l<name>`. |
| Windows | `<name>.dll` | |
| macOS | `<name>.dylib` | |

Non-`static` C functions are exported with the C ABI (`[UnmanagedCallersOnly]`,
cdecl) under their original names; `static` functions stay internal; varargs
functions are skipped (not a valid unmanaged signature).

## Call it from a real C program

```c
/* consumer.c */
#include <stdio.h>
extern int    add(int, int);
extern double scale(double, double);
extern int    double_it(int);

int main(void) {
    printf("add(2,3)=%d\n", add(2, 3));        /* 5  */
    printf("double_it(21)=%d\n", double_it(21)); /* 42 */
    printf("scale(2.5,4)=%g\n", scale(2.5, 4.0)); /* 10 */
    return 0;
}
```

```bash
SO=build/bin/Release/net10.0/<RID>/publish/build.so   # or .dll/.dylib
gcc consumer.c -o consumer "$SO" -Wl,-rpath,"$(dirname "$SO")"
./consumer
# add(2,3)=5
# double_it(21)=42
# scale(2.5,4)=10
```

This exact round-trip is a CI regression test: the opt-in `shared-lib-oracle`
(`DOTCC_RUN_SHARED_LIB_ORACLE=1`, `DotCC.FunctionalTests/SharedLibOracle.cs`)
publishes the library and runs a C consumer against it on every push, so the
native cdecl export ABI stays proven — not just the managed metadata
(`LibraryModeTests`).

> Consuming such a library *from another dotcc-compiled C program* works today via
> **`<dlfcn.h>`**: a dotcc program can `dlopen` this `.so`, `dlsym` an export, and
> call it through the C ABI — dotcc lowers a `dlsym` result cast directly to a
> function type into a `delegate* unmanaged[Cdecl]<…>` so the call meets the
> library's cdecl exports. That dotcc-consumes-dotcc round-trip is itself a
> `shared-lib-oracle` test (see the `dlfcn.h` row in
> [`C-SUPPORT.md`](../../docs/C-SUPPORT.md)). Resolving a library's `extern`s *implicitly*
> (linker-style, no `dlopen`) still needs dotcc's planned **import mode**.
