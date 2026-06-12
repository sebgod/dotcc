#ifndef _DLFCN_H
#define _DLFCN_H

/* dotcc's <dlfcn.h> — the POSIX dynamic-loader API, lowered onto .NET's
   System.Runtime.InteropServices.NativeLibrary (DotCC.Libc.DlfcnLib). One binary
   loads the right object per OS at runtime: a .so on Linux, a .dll on Windows, a
   .dylib on macOS. dotcc defines no compile-time OS-identity macro, so a portable
   program picks the library name at runtime (dlopen("libc.so.6") else
   dlopen("msvcrt.dll")) — which is the faithful dlopen idiom anyway.

   THE CALLING-CONVENTION CONTRACT (important). dlsym() returns a *native* code
   address. To call it correctly you MUST cast the dlsym() result DIRECTLY to the
   function-pointer type, at the call site or the pointer's initializer:

       unsigned long (*fn)(const char *) =
           (unsigned long (*)(const char *))dlsym(h, "strlen");
       fn("hello");                          // 5

   dotcc recognises this exact idiom (a cast of a dlsym() call to a
   function-pointer type) and emits an unmanaged cdecl call through it. Laundering
   the address through a `void *` variable first —

       void *p = dlsym(h, "strlen");
       ((unsigned long (*)(const char *))p)("hi");   // WRONG: managed convention

   — cannot be verified native and draws a compile-time warning, because the call
   would use the wrong (managed) calling convention. Cast the dlsym() call itself,
   not a void* that holds it.

   The RTLD_* flags are accepted and ignored (the platform loader's default
   binding is used); dlopen(NULL, flag) returns a handle to the main program. */

/* dlopen() flags — glibc values, accepted but advisory (see above). */
#define RTLD_LAZY     0x00001
#define RTLD_NOW      0x00002
#define RTLD_NOLOAD   0x00004
#define RTLD_GLOBAL   0x00100
#define RTLD_LOCAL    0x00000
#define RTLD_NODELETE 0x01000

void *dlopen(const char *filename, int flag);
void *dlsym(void *handle, const char *symbol);
int   dlclose(void *handle);
char *dlerror(void);

#endif /* _DLFCN_H */
