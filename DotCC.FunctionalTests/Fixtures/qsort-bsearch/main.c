/* End-to-end coverage for <stdlib.h> qsort + bsearch with a function-pointer
   comparator (dotcc decays the bare `cmp` name to &cmp at the call site). ISO
   C, validated by both oracles. */

#include <stdio.h>
#include <stdlib.h>

static int cmp_int(const void* a, const void* b)
{
    int x = *(const int*)a;
    int y = *(const int*)b;
    return (x > y) - (x < y);
}

int main(void)
{
    int a[] = {5, 3, 8, 1, 9, 2, 7};
    int n = (int)(sizeof(a) / sizeof(a[0]));

    qsort(a, n, sizeof(int), cmp_int);

    printf("sorted:");
    for (int i = 0; i < n; i++) printf(" %d", a[i]);
    printf("\n");

    int keys[] = {8, 4, 1, 9};
    for (int i = 0; i < 4; i++)
    {
        int* hit = (int*)bsearch(&keys[i], a, n, sizeof(int), cmp_int);
        printf("bsearch %d -> %s\n", keys[i], hit ? "found" : "missing");
    }

    return 0;
}
