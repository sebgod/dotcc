/* <stdio.h>'s implementation-defined limit macros (C99 7.21.1) — BUFSIZ,
 * FILENAME_MAX, FOPEN_MAX, TMP_MAX, L_tmpnam — plus the setvbuf buffering modes
 * _IOFBF/_IOLBF/_IONBF. Their exact values are impl-defined (and differ between
 * gcc/glibc, MSVC, and dotcc), so this fixture asserts only the STANDARD-
 * GUARANTEED properties — minima + distinctness — which all three satisfy
 * identically. The motivating use is Lua lauxlib's `char buff[BUFSIZ];` struct
 * member: BUFSIZ must fold to a constant array bound. */
#include <stdio.h>

struct reader {
    int n;
    char buff[BUFSIZ];   /* BUFSIZ as a constant array bound */
};

int main(void) {
    struct reader r;
    r.n = 0;
    /* BUFSIZ >= 256, FOPEN_MAX >= 8, TMP_MAX >= 25 are the standard minima. */
    printf("buff>=256: %d\n", (int)sizeof(r.buff) >= 256);
    printf("FOPEN_MAX>=8: %d\n", FOPEN_MAX >= 8);
    printf("TMP_MAX>=25: %d\n", TMP_MAX >= 25);
    printf("FILENAME_MAX>0: %d\n", FILENAME_MAX > 0);
    printf("L_tmpnam>0: %d\n", L_tmpnam > 0);
    /* The three buffering modes must be distinct. */
    printf("modes distinct: %d\n",
           _IOFBF != _IOLBF && _IOLBF != _IONBF && _IOFBF != _IONBF);
    return 0;
}
