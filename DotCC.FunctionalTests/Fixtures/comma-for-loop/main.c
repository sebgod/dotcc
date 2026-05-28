/* Comma operator in for-loop init / update positions. Covers the
   dominant real-world use case: walking two indices in parallel. C#
   accepts the same comma-list shape in for-init/update natively, so
   the lowering is straight passthrough. */

#include <stdio.h>

int main(void)
{
    /* Two indices, walking inward. */
    int i, j;
    int meetings = 0;
    for (i = 0, j = 10; i < j; i++, j--) {
        printf("i=%d j=%d\n", i, j);
        meetings++;
    }
    printf("meetings=%d final i=%d j=%d\n", meetings, i, j);

    /* Three updates in the update position. */
    int a, b, c;
    for (a = 0, b = 100, c = 50; a < 3; a++, b -= 10, c += 5) {
        printf("a=%d b=%d c=%d\n", a, b, c);
    }

    return 0;
}
