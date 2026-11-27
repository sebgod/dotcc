/* Struct tags spelled like C# contextual keywords (GH #244). required, file,
 * scoped and record are refused as a type name unless escaped; var, nint, nuint
 * and unmanaged are used by the emitted program and the runtime, so a type of
 * that name would capture them. The union and enum forms are the sibling
 * fixtures cs-contextual-tags-union and cs-contextual-tags-enum. */
#include <stdio.h>

struct record { int m; };
struct required { int m; };
struct file { int m; };
struct scoped { int m; };
struct var { int m; };
struct nint { int m; };
struct nuint { int m; };
struct unmanaged { int m; };

#define USE(T, k) { struct T x; struct T *p = &x; p->m = k; total += x.m + (int)sizeof(struct T); }

int main(void)
{
    int total = 0;
    USE(record, 1)
    USE(required, 2)
    USE(file, 3)
    USE(scoped, 4)
    USE(var, 5)
    USE(nint, 6)
    USE(nuint, 7)
    USE(unmanaged, 8)
    printf("%d\n", total);
    return 0;
}
