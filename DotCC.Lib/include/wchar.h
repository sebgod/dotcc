#ifndef _WCHAR_H
#define _WCHAR_H

/* dotcc's <wchar.h> — C99 7.24 extended multibyte/wide-character utilities.

   wchar_t is dotcc's wide character type. dotcc commits to the MSVC shape: an
   unsigned 16-bit UTF-16 code unit, lowered to C# `char` (also a 16-bit UTF-16
   code unit) exactly like char16_t. So `wchar_t*` arithmetic walks 2 bytes, the
   `L"…"` / `L'…'` literals carry real UTF-16, and a wchar_t string bridges to C#
   `string`/`Span<char>`.

   This is a deliberate, documented ABI choice — the same flavour as dotcc's LP64
   and little-endian commitments (see __SIZEOF_WCHAR_T__ == 2). On gcc/Linux
   wchar_t is instead a 32-bit type, so the same source diverges there; dotcc's
   differential test harness opts the gcc oracle out per fixture (the MSVC oracle,
   whose wchar_t is also 16-bit, validates the snapshot).

   wchar_t needs no typedef here: it is pre-registered as a known type name in
   dotcc's TypeNameRewriter (Compiler.PredefinedTypeNames) and resolves straight
   to the WChar primitive (-> C# char) in IrBuilder. This header exists so that
   `#include <wchar.h>` resolves, declares the wide-string library, and documents
   the support.

   The wide-string library, wide character/line I/O, and wide formatted I/O are all
   provided (see the prototypes below): dotcc's narrow encoding is UTF-8, so wide I/O
   is a UTF-8<->UTF-16 conversion against the same byte streams <stdio.h> uses, and
   the wide printf/scanf formats are transcoded to UTF-8 to reuse the byte engine.

   NOT provided (out of scope): the explicit multibyte<->wide conversion model
   (mbrtowc / wcrtomb / mbsrtowcs / mbstate_t and the <uchar.h> mbrtoc16/c16rtomb
   family) — dotcc has no locale/multibyte machinery beyond the implicit UTF-8<->UTF-16
   bridge. char32_t stays out too (see <uchar.h>). */

#include <stddef.h>   /* size_t */
#include <stdio.h>    /* FILE (for the wide stream I/O below) */

/* wint_t — an integer type able to hold any wchar_t value plus WEOF. dotcc uses
   a plain (signed) int with WEOF == -1, mirroring how <stdio.h> models EOF, so
   the `c == WEOF` end-of-input idiom works. (MSVC instead makes wint_t a 16-bit
   unsigned short with WEOF == 0xFFFF; the comparison is internally consistent
   either way — only printing WEOF's raw numeric value would diverge.) */
typedef int wint_t;

#define WEOF      (-1)
#ifndef WCHAR_MIN
#define WCHAR_MIN 0
#endif
#ifndef WCHAR_MAX
#define WCHAR_MAX 0xffff
#endif

/* Wide-string library — the 16-bit siblings of the <string.h> str and mem
   functions. Implementations live in DotCC.Libc/WcharLib.cs, spliced into every emitted
   program via the embedded-resource runtime block. As in <string.h>, the
   signatures drop `const` (dotcc lowers both `wchar_t*` and `const wchar_t*` to
   C# `char*`) and length-returning functions are declared `int`. */

/* Length / comparison / copy. */
int wcslen(wchar_t* s);
int wcscmp(wchar_t* a, wchar_t* b);
int wcsncmp(wchar_t* a, wchar_t* b, size_t n);
int wcscoll(wchar_t* a, wchar_t* b);
wchar_t* wcscpy(wchar_t* dst, wchar_t* src);
wchar_t* wcsncpy(wchar_t* dst, wchar_t* src, size_t n);

/* Concatenation. */
wchar_t* wcscat(wchar_t* dst, wchar_t* src);
wchar_t* wcsncat(wchar_t* dst, wchar_t* src, size_t n);

/* Search. */
wchar_t* wcschr(wchar_t* s, wchar_t c);
wchar_t* wcsrchr(wchar_t* s, wchar_t c);
wchar_t* wcsstr(wchar_t* haystack, wchar_t* needle);
int wcsspn(wchar_t* s, wchar_t* accept);
int wcscspn(wchar_t* s, wchar_t* reject);
wchar_t* wcspbrk(wchar_t* s, wchar_t* accept);

/* Tokenize — reentrant by signature (explicit wchar_t** save slot, unlike the
   stateful narrow strtok). */
wchar_t* wcstok(wchar_t* str, wchar_t* delim, wchar_t** saveptr);

/* Wide memory — counts are in wchar_t units, not bytes. */
wchar_t* wmemcpy(wchar_t* dst, wchar_t* src, size_t n);
wchar_t* wmemmove(wchar_t* dst, wchar_t* src, size_t n);
wchar_t* wmemset(wchar_t* dst, wchar_t c, size_t n);
int wmemcmp(wchar_t* a, wchar_t* b, size_t n);
wchar_t* wmemchr(wchar_t* s, wchar_t c, size_t n);

/* Wide -> number (transcode the ASCII run + delegate to the byte cores). */
long wcstol(wchar_t* nptr, wchar_t** endptr, int base);
long wcstoll(wchar_t* nptr, wchar_t** endptr, int base);
unsigned long wcstoul(wchar_t* nptr, wchar_t** endptr, int base);
unsigned long wcstoull(wchar_t* nptr, wchar_t** endptr, int base);
double wcstod(wchar_t* nptr, wchar_t** endptr);
float wcstof(wchar_t* nptr, wchar_t** endptr);
long double wcstold(wchar_t* nptr, wchar_t** endptr);

/* Wide character / line I/O. dotcc's narrow encoding is UTF-8, so these convert
   UTF-8<->UTF-16 against the same byte streams the <stdio.h> functions use. */
wint_t   fputwc(wchar_t c, FILE* stream);
wint_t   putwc(wchar_t c, FILE* stream);
wint_t   putwchar(wchar_t c);
int      fputws(wchar_t* s, FILE* stream);
wint_t   fgetwc(FILE* stream);
wint_t   getwc(FILE* stream);
wint_t   getwchar(void);
wint_t   ungetwc(wint_t c, FILE* stream);
wchar_t* fgetws(wchar_t* s, int n, FILE* stream);

/* Wide formatted I/O. The wide format is transcoded to UTF-8 and reuses the byte
   printf/scanf engine; a wide %s/%c argument is a wchar_t*. swprintf bounds the
   write to n wide chars including the NUL (returning negative on overflow, per C —
   unlike snprintf). */
int wprintf(wchar_t* fmt, ...);
int fwprintf(FILE* stream, wchar_t* fmt, ...);
int swprintf(wchar_t* s, size_t n, wchar_t* fmt, ...);
int wscanf(wchar_t* fmt, ...);
int fwscanf(FILE* stream, wchar_t* fmt, ...);
int swscanf(wchar_t* src, wchar_t* fmt, ...);

#endif
