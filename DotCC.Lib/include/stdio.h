#ifndef _STDIO_H
#define _STDIO_H

/* dotcc's <stdio.h> — declares the standard I/O surface so the parser
   knows the signatures. The actual implementations live in
   DotCC.Libc.Libc (printf, fprintf, puts, scanf, sprintf, putchar, …),
   and are inlined into every emitted program by Compiler.BuildShell
   until DotCC.Libc ships as a NuGet package.

   Note on FILE*: dotcc models a C `FILE*` as a managed System.IO
   TextWriter (output streams) / TextReader (input streams) rather than a
   single `FILE` type. The stream-taking functions (fprintf, fputs,
   fscanf, fputc/fgetc/putc/getc/fgets) therefore aren't declared here —
   they resolve through `using static Libc;` at C# overload-resolution
   time, exactly as fprintf has always done. Only the FILE-free entries
   are declared below. Full file I/O (fopen/fread/fwrite/fseek) is a
   separate, larger effort tracked in C-SUPPORT.md. */

#ifndef NULL
#define NULL null
#endif

#define EOF (-1)

/* Formatted output (to stdout / a buffer). */
int printf(char* fmt, ...);
int sprintf(char* dst, char* fmt, ...);
int snprintf(char* dst, int n, char* fmt, ...);

/* Formatted input (from stdin / a buffer). */
int scanf(char* fmt, ...);
int sscanf(char* src, char* fmt, ...);

/* Whole-string output. */
int puts(char* s);

/* Character I/O on stdin / stdout. */
int putchar(int c);
int getchar(void);

/* Error reporting (uses errno; see <errno.h>). */
void perror(char* s);

#endif
