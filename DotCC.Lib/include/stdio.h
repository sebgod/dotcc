#ifndef _STDIO_H
#define _STDIO_H

/* dotcc's <stdio.h> — declares the standard I/O surface so the parser
   knows the signatures. The actual implementations live in
   DotCC.Libc.Libc (printf, fprintf, fopen, fread, puts, putchar, …),
   and are inlined into every emitted program by Compiler.BuildShell
   until DotCC.Libc ships as a NuGet package.

   FILE model: dotcc keeps `FILE` an opaque struct (Libc.FILE) and lets
   `FILE*` stay a genuine pointer — so NULL, ==, and `if (fp)` all work
   through the normal pointer machinery, no special-casing. The `FILE`
   name is pre-registered (Compiler.PredefinedTypeNames) so it needs no
   typedef here. stdin/stdout/stderr are FILE* and resolve through
   `using static Libc;`; their text routes through Console.In/Out/Error
   (so redirection is honored), while fopen'd streams wrap a real file.
   stdin/stdout/stderr/fopen/fprintf/fputs/fputc/fgetc/fgets/fscanf all
   resolve at C# overload-resolution time and so aren't re-declared. */

#ifndef NULL
#define NULL null
#endif

#define EOF (-1)

/* fseek origins. */
#define SEEK_SET 0
#define SEEK_CUR 1
#define SEEK_END 2

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

/* File streams (FILE* is a real pointer to the opaque Libc.FILE). */
FILE* fopen(const char* path, const char* mode);
FILE* freopen(const char* path, const char* mode, FILE* stream);
FILE* tmpfile(void);
int fclose(FILE* stream);
int fflush(FILE* stream);
int fread(void* ptr, int size, int nmemb, FILE* stream);
int fwrite(void* ptr, int size, int nmemb, FILE* stream);
int fseek(FILE* stream, long offset, int whence);
long ftell(FILE* stream);
void rewind(FILE* stream);
int feof(FILE* stream);
int ferror(FILE* stream);
void clearerr(FILE* stream);
int remove(const char* path);
int rename(const char* oldp, const char* newp);

/* Error reporting (uses errno; see <errno.h>). */
void perror(char* s);

#endif
