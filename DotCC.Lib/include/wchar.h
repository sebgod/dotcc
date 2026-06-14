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

   NOT provided (out of scope): wide formatted I/O (wprintf / fwprintf / swprintf
   and the w*scanf family — the printf engine is UTF-8/`L(...)`-based, so a wide
   format needs a separate engine), and wide INPUT (fgetws / getwc / getwchar).
   The multibyte<->wide conversion model (mbrtowc / wcrtomb / mbstate_t) is also
   not provided — dotcc has no locale/multibyte machinery. char32_t stays out too
   (see <uchar.h>). */

#include <stddef.h>   /* size_t */

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

#endif
