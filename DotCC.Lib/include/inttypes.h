#ifndef _INTTYPES_H
#define _INTTYPES_H

/* dotcc's <inttypes.h> (C99 7.8) — format-string macros for the
   <stdint.h> fixed-width types, plus greatest-width integer functions
   (DotCC.Libc/InttypesLib.cs). Includes <stdint.h> per the standard.

   Length modifiers below follow dotcc's LP64 model: the 8/16/32-bit
   types promote to int in a varargs call so they need no modifier for
   printf; the 64-bit / MAX / PTR types are 64-bit and use the "ll"
   modifier. (dotcc's printf builder parses and strips the l/ll modifier
   and formats by the actual argument type, so either spelling is safe.)

   Note: %o (octal) output isn't supported by dotcc's printf yet, so the
   PRIo* macros are defined for source compatibility but print decimal if
   used — see C-SUPPORT.md. The d/i/u/x/X families work. */

#include <stdint.h>

/* ---- fprintf macros ---- */
#define PRId8   "d"
#define PRId16  "d"
#define PRId32  "d"
#define PRId64  "lld"
#define PRIdMAX "lld"
#define PRIdPTR "lld"

#define PRIi8   "i"
#define PRIi16  "i"
#define PRIi32  "i"
#define PRIi64  "lli"
#define PRIiMAX "lli"
#define PRIiPTR "lli"

#define PRIo8   "o"
#define PRIo16  "o"
#define PRIo32  "o"
#define PRIo64  "llo"
#define PRIoMAX "llo"
#define PRIoPTR "llo"

#define PRIu8   "u"
#define PRIu16  "u"
#define PRIu32  "u"
#define PRIu64  "llu"
#define PRIuMAX "llu"
#define PRIuPTR "llu"

#define PRIx8   "x"
#define PRIx16  "x"
#define PRIx32  "x"
#define PRIx64  "llx"
#define PRIxMAX "llx"
#define PRIxPTR "llx"

#define PRIX8   "X"
#define PRIX16  "X"
#define PRIX32  "X"
#define PRIX64  "llX"
#define PRIXMAX "llX"
#define PRIXPTR "llX"

/* ---- fscanf macros ---- */
#define SCNd8   "hhd"
#define SCNd16  "hd"
#define SCNd32  "d"
#define SCNd64  "lld"
#define SCNdMAX "lld"
#define SCNdPTR "lld"

#define SCNi8   "hhi"
#define SCNi16  "hi"
#define SCNi32  "i"
#define SCNi64  "lli"
#define SCNiMAX "lli"
#define SCNiPTR "lli"

#define SCNo8   "hho"
#define SCNo16  "ho"
#define SCNo32  "o"
#define SCNo64  "llo"
#define SCNoMAX "llo"
#define SCNoPTR "llo"

#define SCNu8   "hhu"
#define SCNu16  "hu"
#define SCNu32  "u"
#define SCNu64  "llu"
#define SCNuMAX "llu"
#define SCNuPTR "llu"

#define SCNx8   "hhx"
#define SCNx16  "hx"
#define SCNx32  "x"
#define SCNx64  "llx"
#define SCNxMAX "llx"
#define SCNxPTR "llx"

/* ---- greatest-width integer functions ----
   imaxdiv_t is pre-registered in dotcc's TypeNameRewriter
   (Compiler.PredefinedTypeNames) and resolves to Libc.imaxdiv_t. */
intmax_t imaxabs(intmax_t n);
imaxdiv_t imaxdiv(intmax_t num, intmax_t den);
intmax_t strtoimax(const char *nptr, char **endptr, int base);
uintmax_t strtoumax(const char *nptr, char **endptr, int base);

#endif
