#ifndef _UCHAR_H
#define _UCHAR_H

/* dotcc's <uchar.h> — C11 7.28. Unicode utilities.

   char16_t is dotcc's one supported wide character type: a 16-bit UTF-16 code
   unit. It lowers to C# `char` (also a 16-bit UTF-16 code unit), so `char16_t*`
   arithmetic walks 2 bytes and the `u"…"` / `u'…'` literals carry real UTF-16.

   char16_t needs no typedef here: it is pre-registered as a known type name in
   dotcc's TypeNameRewriter (Compiler.PredefinedTypeNames) and resolves straight
   to the Char16 primitive (→ C# char) in IrBuilder. This header exists so that
   `#include <uchar.h>` resolves and documents the support.

   NOT provided (out of scope — dotcc has no multibyte/locale conversion model):
   char32_t and the conversion functions mbrtoc16 / c16rtomb / mbrtoc32 /
   c32rtomb. wchar_t is also unsupported (its width is platform-divergent —
   16-bit on Windows, 32-bit elsewhere). */

#endif
