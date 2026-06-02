// Newly-added libc surface needed by the Lua link: frexp/ldexp (math),
// strcoll (string; "C"-locale == strcmp), ungetc + setvbuf (stdio). Oracled.

#include <stdio.h>
#include <math.h>
#include <string.h>

int main(void) {
    // setvbuf must run before any I/O on the stream to be well-defined; a
    // valid call returns 0 (dotcc's streams are managed, so it's a no-op).
    int sv = setvbuf(stdout, NULL, _IONBF, 0);

    // frexp splits x into mantissa in [0.5,1) and exponent: 12 = 0.75 * 2^4.
    int e;
    double m = frexp(12.0, &e);
    printf("frexp: %g %d\n", m, e);
    printf("ldexp: %g\n", ldexp(m, e));   // round-trips back to 12

    // strcoll == strcmp in the C locale.
    printf("strcoll: %d %d\n", strcoll("abc", "abc") == 0, strcoll("abc", "abd") < 0);

    // ungetc: read a byte, push it back, then re-read it before the next byte.
    FILE *f = tmpfile();
    fputc('X', f);
    fputc('Y', f);
    rewind(f);
    int c1 = fgetc(f);   // 'X'
    ungetc(c1, f);       // push 'X' back
    int c2 = fgetc(f);   // 'X' again
    int c3 = fgetc(f);   // 'Y'
    printf("ungetc: %c%c%c\n", c1, c2, c3);
    fclose(f);

    printf("setvbuf: %d\n", sv);
    return 0;
}
