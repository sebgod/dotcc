/* sizeof applied to EXPRESSIONS (not just types) — driven by a type-synthesis
 * layer. The headline case is the array-length idiom sizeof(a)/sizeof(a[0]).
 * Arrays lower to C# pointers, so dotcc computes count*sizeof(element); other
 * expressions defer to C# sizeof(type). Casts to int avoid the size_t/%d
 * mismatch (and match dotcc, where sizeof yields an int). gcc is the oracle. */
#include <stdio.h>

struct Pt { int x; int y; };

int main(void) {
    int arr[5];
    const char *strs[] = { "a", "b", "c" };
    double dv;
    int *p;
    struct Pt pt;

    printf("%d\n", (int)(sizeof(arr) / sizeof(arr[0])));    /* 5  — the idiom */
    printf("%d\n", (int)sizeof(arr));                        /* 20 — 5 * 4 */
    printf("%d\n", (int)(sizeof(strs) / sizeof(strs[0])));   /* 3  — char* array */
    printf("%d\n", (int)sizeof(dv));                          /* 8  — double var */
    printf("%d\n", (int)sizeof(p));                           /* 8  — pointer */
    printf("%d\n", (int)sizeof(*p));                          /* 4  — *int* = int */
    printf("%d\n", (int)sizeof('a'));                         /* 4  — char literal is int! */
    printf("%d\n", (int)sizeof((char)'a'));                   /* 1  — cast to char */
    printf("%d\n", (int)sizeof("hello"));                     /* 6  — 5 chars + NUL */
    printf("%d\n", (int)sizeof(42));                          /* 4  — int literal */
    printf("%d\n", (int)sizeof(42L));                         /* 8  — long literal */
    printf("%d\n", (int)sizeof(3.14));                        /* 8  — double literal */
    printf("%d\n", (int)sizeof(int));                         /* 4  — type form still works */
    printf("%d\n", (int)sizeof(pt));                          /* 8  — struct value */
    return 0;
}
