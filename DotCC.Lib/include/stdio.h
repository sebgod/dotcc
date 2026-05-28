#ifndef _STDIO_H
#define _STDIO_H

/* dotcc's <stdio.h> — declares the standard I/O surface so the parser
   knows the signatures. The actual implementations live in
   DotCC.Libc.Libc (printf, fprintf, puts, scanf, sprintf, etc.), and
   are inlined into every emitted program by Compiler.BuildShell until
   DotCC.Libc ships as a NuGet package. */

int printf(char* fmt, ...);

#endif
