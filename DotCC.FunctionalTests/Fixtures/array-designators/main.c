#include <stdio.h>

/* C99 array designators — `[index] = value` in an array initializer. dotcc
   builds a dense, zero-filled array at compile time: a designator sets the
   cursor, an undesignated element fills the current slot, both advance it
   (later writes to the same index win). Implicit `[]` size derives from the
   highest index touched. */

int main(void) {
    int a[5] = {[2] = 9, [4] = 1};            /* sparse */
    int b[6] = {[1] = 10, 20, 30, [0] = 5};   /* designator + running + reorder */
    int c[] = {[3] = 7};                       /* implicit size from max index */

    printf("%d %d %d %d %d\n", a[0], a[1], a[2], a[3], a[4]);
    printf("%d %d %d %d %d %d\n", b[0], b[1], b[2], b[3], b[4], b[5]);
    printf("%d %d %d %d\n", c[0], c[1], c[2], c[3]);
    return 0;
}
