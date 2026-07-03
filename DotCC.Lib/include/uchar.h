#ifndef _UCHAR_H
#define _UCHAR_H

/* dotcc's <uchar.h> — C11 7.28. Unicode utilities.

   char8_t (C23) is an 8-bit UTF-8 code unit. It lowers to C# `byte` (like
   dotcc's `char`), so a `u8"…"` / `u8'…'` literal rides the existing narrow
   UTF-8 string path — dotcc's plain string literals are ALREADY UTF-8.

   char16_t is a 16-bit UTF-16 code unit. It lowers to C# `char` (also a 16-bit
   UTF-16 code unit), so `char16_t*` arithmetic walks 2 bytes and the `u"…"` /
   `u'…'` literals carry real UTF-16.

   char32_t is a 32-bit UTF-32 code unit. It lowers to C# `uint` (a plain 32-bit
   unsigned integer, NOT System.Text.Rune — a char32_t is any value), so
   `char32_t*` arithmetic walks 4 bytes and the `U"…"` / `U'…'` literals carry
   real UTF-32: one code unit per Unicode scalar (an astral char is ONE char32_t,
   not the two surrogate units of a char16_t string).

   None of them needs a typedef here: all three are pre-registered as known type
   names in dotcc's TypeNameRewriter (Compiler.PredefinedTypeNames) and resolve
   straight to the Char8 / Char16 / Char32 primitives (→ C# byte / char / uint)
   in IrBuilder. This header exists so that `#include <uchar.h>` resolves and
   documents the support.

   NOT provided (out of scope — dotcc has no multibyte/locale conversion model):
   the conversion functions mbrtoc16 / c16rtomb / mbrtoc32 / c32rtomb. (wchar_t IS
   supported — see <wchar.h> — as dotcc's MSVC-shaped 16-bit wide type, the
   sibling of char16_t.) */

#endif
