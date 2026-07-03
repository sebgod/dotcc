#ifndef _STDINT_H
#define _STDINT_H

/* dotcc's <stdint.h> — C99 fixed-width integer typedefs.
   Each width maps to a primitive:
     int8_t    signed 8-bit      → C# sbyte
     uint8_t   unsigned 8-bit    → C# byte
     int16_t   signed 16-bit     → C# short
     uint16_t  unsigned 16-bit   → C# ushort
     int32_t   signed 32-bit     → C# int
     uint32_t  unsigned 32-bit   → C# uint
     int64_t   signed 64-bit     → C# long
     uint64_t  unsigned 64-bit   → C# ulong

   Pointer-sized + size types: dotcc targets .NET on 64-bit hosts, so
   `long` is unconditionally 64-bit (see CLAUDE.md / C-SUPPORT.md). That
   matches Linux's LP64 model, NOT MSVC-Windows's LLP64. Code that
   round-trips intptr_t through long on dotcc behaves like Linux glibc.
   Programs that hard-depend on MSVC's `long`-is-32-bit may need
   adjustments.

   size_t / ptrdiff_t are NOT stdint types — they live in <stddef.h>, their
   canonical C home. We include it so code that reaches for them through
   <stdint.h> (common in practice) still resolves them; the SIZE_MAX /
   PTRDIFF_MAX limit macros below pair with those <stddef.h> types. */

#include <stddef.h>

typedef signed char int8_t;
typedef unsigned char uint8_t;
typedef short int16_t;
typedef unsigned short uint16_t;
typedef int int32_t;
typedef unsigned int uint32_t;
typedef long int64_t;
typedef unsigned long uint64_t;

typedef long intptr_t;
typedef unsigned long uintptr_t;
typedef long intmax_t;
typedef unsigned long uintmax_t;

/* Minimum-width integer types (C99 7.18.1.2) — the smallest type with AT LEAST
   the given width. For 8/16/32/64 that is exactly the fixed-width type. */
typedef signed char int_least8_t;
typedef unsigned char uint_least8_t;
typedef short int_least16_t;
typedef unsigned short uint_least16_t;
typedef int int_least32_t;
typedef unsigned int uint_least32_t;
typedef long int_least64_t;
typedef unsigned long uint_least64_t;

/* Fastest minimum-width integer types (C99 7.18.1.3). dotcc follows glibc's
   LP64 (x86-64 / aarch64-Linux) choices EXACTLY, so `sizeof` and the limit
   macros stay byte-identical to the gcc oracle: fast8 is `signed char`, but
   fast16/32/64 are all `long` (the machine word) — NOT the narrow fixed-width
   type — because the wider type is faster to load/store on a 64-bit target. */
typedef signed char int_fast8_t;
typedef unsigned char uint_fast8_t;
typedef long int_fast16_t;
typedef unsigned long uint_fast16_t;
typedef long int_fast32_t;
typedef unsigned long uint_fast32_t;
typedef long int_fast64_t;
typedef unsigned long uint_fast64_t;

/* Limit macros (C99 7.18.2). Numeric literals so they're usable as
   integer constant expressions per the C standard. */
#define INT8_MIN   (-128)
#define INT8_MAX   127
#define UINT8_MAX  255
#define INT16_MIN  (-32768)
#define INT16_MAX  32767
#define UINT16_MAX 65535
#define INT32_MIN  (-2147483647 - 1)
#define INT32_MAX  2147483647
#define UINT32_MAX 4294967295u
#define INT64_MIN  (-9223372036854775807L - 1)
#define INT64_MAX  9223372036854775807L
#define UINT64_MAX 18446744073709551615uL

#define INTPTR_MIN   INT64_MIN
#define INTPTR_MAX   INT64_MAX
#define UINTPTR_MAX  UINT64_MAX
#define SIZE_MAX     UINT64_MAX
#define PTRDIFF_MIN  INT64_MIN
#define PTRDIFF_MAX  INT64_MAX
#define INTMAX_MIN   INT64_MIN
#define INTMAX_MAX   INT64_MAX
#define UINTMAX_MAX  UINT64_MAX

/* Minimum-width limits (C99 7.18.2.2) — identical to the fixed-width limits. */
#define INT_LEAST8_MIN    INT8_MIN
#define INT_LEAST8_MAX    INT8_MAX
#define UINT_LEAST8_MAX   UINT8_MAX
#define INT_LEAST16_MIN   INT16_MIN
#define INT_LEAST16_MAX   INT16_MAX
#define UINT_LEAST16_MAX  UINT16_MAX
#define INT_LEAST32_MIN   INT32_MIN
#define INT_LEAST32_MAX   INT32_MAX
#define UINT_LEAST32_MAX  UINT32_MAX
#define INT_LEAST64_MIN   INT64_MIN
#define INT_LEAST64_MAX   INT64_MAX
#define UINT_LEAST64_MAX  UINT64_MAX

/* Fastest minimum-width limits (C99 7.18.2.3). fast8 uses the 8-bit limits;
   fast16/32/64 use the underlying `long` (64-bit) limits, matching the LP64
   typedefs above (glibc x86-64 parity). */
#define INT_FAST8_MIN     INT8_MIN
#define INT_FAST8_MAX     INT8_MAX
#define UINT_FAST8_MAX    UINT8_MAX
#define INT_FAST16_MIN    INT64_MIN
#define INT_FAST16_MAX    INT64_MAX
#define UINT_FAST16_MAX   UINT64_MAX
#define INT_FAST32_MIN    INT64_MIN
#define INT_FAST32_MAX    INT64_MAX
#define UINT_FAST32_MAX   UINT64_MAX
#define INT_FAST64_MIN    INT64_MIN
#define INT_FAST64_MAX    INT64_MAX
#define UINT_FAST64_MAX   UINT64_MAX

#endif
