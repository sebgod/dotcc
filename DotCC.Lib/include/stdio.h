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

/* Implementation-defined limits (C99 7.21.1). The exact values are
   impl-defined — portable code must treat them as opaque constants —
   so dotcc just picks reasonable ones that satisfy the standard's
   minima (BUFSIZ >= 256, FOPEN_MAX >= 8, TMP_MAX >= 25). BUFSIZ is the
   default stream buffer size; code like Lua's `char buff[BUFSIZ]` only
   needs it to fold to a constant array bound. */
#define BUFSIZ 8192
#define FILENAME_MAX 4096
#define FOPEN_MAX 16
#define TMP_MAX 238328
/* Large enough to hold an OS temp-directory path + a random component (dotcc's
   tmpnam returns a full path); keep in sync with LTmpnam in FileLib.cs. */
#define L_tmpnam 260

/* setvbuf buffering modes (C99 7.21.5.6). */
#define _IOFBF 0
#define _IOLBF 1
#define _IONBF 2

/* Formatted output (to stdout / a buffer). */
int printf(char* fmt, ...);
int sprintf(char* dst, char* fmt, ...);
int snprintf(char* dst, int n, char* fmt, ...);

/* Formatted input (from stdin / a buffer). */
int scanf(char* fmt, ...);
int sscanf(char* src, char* fmt, ...);

/* Whole-string output. */
int puts(char* s);
int fputs(char* s, FILE* stream);

/* Character I/O. Declared so dotcc knows the `int`/`FILE*` parameter types and
   coerces arguments (e.g. a `size_t` sizeof passed to fgets's `int n`). */
int putchar(int c);
int getchar(void);
int fputc(int c, FILE* stream);
int putc(int c, FILE* stream);
int fgetc(FILE* stream);
int getc(FILE* stream);
char* fgets(char* s, int n, FILE* stream);

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
int ungetc(int c, FILE* stream);
int setvbuf(FILE* stream, char* buf, int mode, int size);
char* tmpnam(char* s);
int remove(const char* path);
int rename(const char* oldp, const char* newp);

/* Error reporting (uses errno; see <errno.h>). */
void perror(char* s);

#endif
