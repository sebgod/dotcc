// dlopen/dlsym/dlclose over .NET's NativeLibrary: load the host's system C
// library at RUNTIME (dotcc defines no compile-time OS-identity macro, so a
// portable program probes by name — libc.so.6 on Linux, msvcrt.dll on Windows;
// both export the CRT strlen), resolve strlen, and call it through a function
// pointer. The dlsym() result is cast DIRECTLY to the function type, which dotcc
// recognises and lowers to an unmanaged cdecl delegate* so the native call uses
// the C calling convention (see DotCC.Lib/include/dlfcn.h).
//
// dlopen/dlsym are POSIX, not ISO C; glibc folds libdl into libc as of 2.34, so
// no -ldl is needed (the ubuntu runners and WSL are well past that). MSVC has no
// <dlfcn.h>, so the MSVC oracle opts out; gcc on Linux (x64 + arm64)
// differential-tests the emitted semantics.
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <dlfcn.h>

int main(void) {
    void *h = dlopen("libc.so.6", RTLD_LAZY);
    if (h == NULL) { h = dlopen("msvcrt.dll", RTLD_LAZY); }
    if (h == NULL) {
        printf("dlopen failed: %s\n", dlerror());
        return 1;
    }

    // size_t is `unsigned long` on LP64 — the faithful strlen signature (no
    // UB-prototype shortcut). Cast the dlsym() result directly to the function
    // type (the recognised native-cdecl idiom).
    unsigned long (*c_strlen)(const char *) =
        (unsigned long (*)(const char *))dlsym(h, "strlen");
    if (c_strlen == NULL) {
        printf("dlsym failed: %s\n", dlerror());
        return 1;
    }

    printf("strlen(hello)=%lu\n", c_strlen("hello"));
    dlclose(h);
    return 0;
}
