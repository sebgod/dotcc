/* sizeof(T) * CHAR_BIT regression: l_numbits(t) computes the bit-width of a
   type. This pattern is used by Lua's VM (`NBITS`) and by any portable C.
   dotcc must not drop the `* CHAR_BIT` multiplication. */
#include <stdio.h>
#include <limits.h>
#include <stdint.h>

int main(void) {
    /* basic sanity */
    printf("CHAR_BIT=%d\n", CHAR_BIT);
    printf("sizeof(int)=%d\n", (int)sizeof(int));
    printf("sizeof(long)=%d\n", (int)sizeof(long));
    printf("sizeof(long long)=%d\n", (int)sizeof(long long));

    /* l_numbits(t) = (int)(sizeof(t) * CHAR_BIT) — the Lua idiom */
    printf("bits_int=%d\n", (int)(sizeof(int) * CHAR_BIT));
    printf("bits_long=%d\n", (int)(sizeof(long) * CHAR_BIT));
    printf("bits_longlong=%d\n", (int)(sizeof(long long) * CHAR_BIT));

    /* array bound using sizeof*CHAR_BIT */
    int arr[(int)(sizeof(long) * CHAR_BIT)];
    printf("arr_len=%d\n", (int)(sizeof(arr) / sizeof(arr[0])));

    /* negative NBITS guard — Lua's `y <= -NBITS` pattern */
    int nbits = (int)(sizeof(long) * CHAR_BIT);
    printf("nbits=%d\n", nbits);
    printf("neg_nbits=%d\n", -nbits);

    return 0;
}
