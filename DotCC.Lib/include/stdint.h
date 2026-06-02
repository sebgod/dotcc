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

#endif
