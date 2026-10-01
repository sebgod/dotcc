/* qsort: sorts n elements of size bytes with cmp. A merge sort, so equal elements keep their order, as glibc's does (it merges too, when it can allocate), through a buffer of n elements; without one, an insertion sort. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>
#include <string.h>

static void merge(char *a, char *buf, int lo, int mid, int hi, int size, int (*cmp)(const void *, const void *))
{
    int i = lo;
    int j = mid;
    int k = lo;
    while (i < mid && j < hi)
    {
        if (cmp(a + j * size, a + i * size) < 0) { memcpy(buf + k * size, a + j * size, size); j++; }
        else { memcpy(buf + k * size, a + i * size, size); i++; }
        k++;
    }
    while (i < mid) { memcpy(buf + k * size, a + i * size, size); i++; k++; }
    while (j < hi) { memcpy(buf + k * size, a + j * size, size); j++; k++; }
    memcpy(a + lo * size, buf + lo * size, (hi - lo) * size);
}

void qsort(void *base, int n, int size, int (*cmp)(const void *, const void *))
{
    char *a = base;
    if (n < 2) { return; }
    char *buf = malloc(n * size);
    if (buf)
    {
        for (int width = 1; width < n; width *= 2)
        {
            for (int lo = 0; lo < n - width; lo += 2 * width)
            {
                int hi = lo + 2 * width < n ? lo + 2 * width : n;
                merge(a, buf, lo, lo + width, hi, size, cmp);
            }
        }
        free(buf);
        return;
    }
    for (int i = 1; i < n; i++)
    {
        for (int j = i; j > 0 && cmp(a + (j - 1) * size, a + j * size) > 0; j--)
        {
            char t;
            for (int b = 0; b < size; b++) { t = a[(j - 1) * size + b]; a[(j - 1) * size + b] = a[j * size + b]; a[j * size + b] = t; }
        }
    }
}
