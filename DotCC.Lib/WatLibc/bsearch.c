/* bsearch: a binary search of n sorted elements of size bytes for key, or NULL. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>

void *bsearch(const void *key, const void *base, int n, int size, int (*cmp)(const void *, const void *))
{
    int lo = 0;
    int hi = n;
    while (lo < hi)
    {
        int mid = lo + (hi - lo) / 2;
        char *at = (char *)base + mid * size;
        int c = cmp(key, at);
        if (c == 0) { return at; }
        if (c < 0) { hi = mid; } else { lo = mid + 1; }
    }
    return NULL;
}
