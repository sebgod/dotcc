#ifndef _LIMITS_H
#define _LIMITS_H

/* dotcc's <limits.h> — C99 7.10. Numeric limits of the primitive int
   family. All values are integer constant expressions so they're
   usable in `static_assert`, switch cases, array dimensions, etc.

   dotcc maps `long` unconditionally to 64-bit C# `long` (LP64 model),
   so LONG_MIN/MAX = LLONG_MIN/MAX. That's the same shape as Linux
   glibc; MSVC-Windows's `long` is 32-bit, so user code that depends
   on `LONG_MAX == 2147483647` will behave differently on dotcc. This
   is a documented choice — see CLAUDE.md "long" notes. */

/* `char` width — bits per char. The C standard mandates ≥ 8; we
   emit `char` as C# `byte` so it's exactly 8. */
#define CHAR_BIT   8

/* Multi-byte char max length — historical, mostly tied to locale
   handling we don't implement. 1 is the safe answer for our UTF-8-
   in-`char*` model where each byte stands alone. */
#define MB_LEN_MAX 1

/* `signed char` / `unsigned char` / plain `char` (which dotcc treats
   as unsigned since we map it to C# `byte`). */
#define SCHAR_MIN  (-128)
#define SCHAR_MAX  127
#define UCHAR_MAX  255
#define CHAR_MIN   0
#define CHAR_MAX   UCHAR_MAX

/* `short` / `unsigned short` — always 16-bit on both dotcc and MSVC. */
#define SHRT_MIN   (-32768)
#define SHRT_MAX   32767
#define USHRT_MAX  65535

/* `int` / `unsigned int` — always 32-bit. */
#define INT_MIN    (-2147483647 - 1)
#define INT_MAX    2147483647
#define UINT_MAX   4294967295u

/* `long` / `unsigned long` — 64-bit on dotcc (LP64). */
#define LONG_MIN   (-9223372036854775807L - 1)
#define LONG_MAX   9223372036854775807L
#define ULONG_MAX  18446744073709551615uL

/* `long long` / `unsigned long long` — same as `long` on dotcc. */
#define LLONG_MIN  LONG_MIN
#define LLONG_MAX  LONG_MAX
#define ULLONG_MAX ULONG_MAX

#endif
