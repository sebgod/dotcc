/* End-to-end smoke test for the synthetic <string.h>: every function
   declared in the header gets exercised, with output formatted so the
   MSVC oracle and dotcc agree byte-for-byte.
   Real C's `strlen` returns `size_t` (unsigned long long on MSVC);
   dotcc's returns `int`. Cast to int at the printf call site so the
   format spec matches on both compilers — without the cast MSVC would
   read 8 bytes of vararg where `%d` only consumes 4.
   `_CRT_SECURE_NO_WARNINGS` silences MSVC's strcpy_s deprecation; the
   warning is informational and the cl.exe compile still succeeds, but
   suppressing it keeps the oracle's stderr clean. */

#define _CRT_SECURE_NO_WARNINGS

#include <stdio.h>
#include <string.h>

int main(void)
{
    /* strlen + strcmp on string literals. */
    printf("strlen = %d\n", (int)strlen("dotcc"));
    printf("eq sign = %d\n", strcmp("abc", "abc"));
    printf("lt sign = %d\n", strcmp("abc", "abd") < 0 ? -1 : 1);

    /* strcpy into a stack buffer, then print the result. */
    char buf[16];
    strcpy(buf, "copied");
    printf("strcpy = %s (len %d)\n", buf, (int)strlen(buf));

    /* memset a small buffer, then verify a byte landed. */
    char zeros[8];
    memset(zeros, 0, 8);
    printf("memset[0] = %d\n", zeros[0]);

    /* memcpy from one buffer to another. */
    char src[6];
    strcpy(src, "memcp");
    char dst[6];
    memset(dst, 0, 6);
    memcpy(dst, src, 6);
    printf("memcpy = %s\n", dst);

    return 0;
}
