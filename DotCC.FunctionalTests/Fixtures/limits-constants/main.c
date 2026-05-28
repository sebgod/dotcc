/* <limits.h> — numeric limits of the primitive int family.
   Tests the macros plus the preprocessor's whitespace-aware
   function-like detection (`#define SCHAR_MIN (-128)` must NOT be
   parsed as a function-like macro definition). */

#include <stdio.h>
#include <limits.h>

int main(void)
{
    /* Bit width. */
    printf("CHAR_BIT=%d\n", CHAR_BIT);

    /* signed char / unsigned char.
       NOTE: plain `char` signedness is implementation-defined.
       dotcc treats `char` as unsigned (C# `byte`); MSVC defaults to
       signed. So `CHAR_MIN`/`CHAR_MAX` would diverge between the two
       compilers — exercising only the explicit signed/unsigned forms
       here. The dotcc-side limit macros for plain `char` (CHAR_MIN=0,
       CHAR_MAX=255) are still exposed in <limits.h>; see
       C-SUPPORT.md for the documented divergence. */
    printf("SCHAR_MIN=%d SCHAR_MAX=%d UCHAR_MAX=%d\n",
        SCHAR_MIN, SCHAR_MAX, UCHAR_MAX);

    /* short / unsigned short. */
    printf("SHRT_MIN=%d SHRT_MAX=%d USHRT_MAX=%d\n",
        SHRT_MIN, SHRT_MAX, USHRT_MAX);

    /* int / unsigned int. */
    printf("INT_MIN=%d INT_MAX=%d\n", INT_MIN, INT_MAX);

    /* Test usability in expressions. */
    printf("INT_MAX/2=%d\n", INT_MAX / 2);
    printf("SCHAR_MIN+1=%d\n", SCHAR_MIN + 1);

    return 0;
}
