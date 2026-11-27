/* Enum tags spelled like C# contextual keywords (GH #244); see cs-contextual-tags. */
#include <stdio.h>

enum record { record_v = 1 };
enum required { required_v = 2 };
enum file { file_v = 3 };
enum scoped { scoped_v = 4 };
enum var { var_v = 5 };
enum nint { nint_v = 6 };
enum nuint { nuint_v = 7 };
enum unmanaged { unmanaged_v = 8 };

#define USE(T) { enum T e = T##_v; enum T *p = &e; total += (int)*p + (int)sizeof(enum T); }

int main(void)
{
    int total = 0;
    USE(record)
    USE(required)
    USE(file)
    USE(scoped)
    USE(var)
    USE(nint)
    USE(nuint)
    USE(unmanaged)
    printf("%d\n", total);
    return 0;
}
