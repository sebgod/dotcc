/* Union tags spelled like C# contextual keywords (GH #244); see cs-contextual-tags. */
#include <stdio.h>

union record { int m; char c[8]; };
union required { int m; char c[8]; };
union file { int m; char c[8]; };
union scoped { int m; char c[8]; };
union var { int m; char c[8]; };
union nint { int m; char c[8]; };
union nuint { int m; char c[8]; };
union unmanaged { int m; char c[8]; };
union extension { int m; char c[8]; };

#define USE(T, k) { union T x; union T *p = &x; p->m = k; total += p->m + (int)sizeof(union T); }

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
    USE(extension, 9)
    printf("%d\n", total);
    return 0;
}
