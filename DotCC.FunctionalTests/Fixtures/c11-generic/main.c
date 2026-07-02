/* C11 _Generic — type-generic selection, resolved at compile time.
 * Exercises: multi-arm type dispatch (incl. unsigned int and pointer
 * arms), string-literal decay to char*, usual-arithmetic promotion of
 * the controlling expression (int + double -> double), the tgmath-style
 * per-type function dispatch macro, array decay, and qualifier drop on
 * a const compound literal. Verified byte-identical vs gcc -std=c17. */
#include <stdio.h>
#include <math.h>

#define typename(x) _Generic((x), \
    int: "int", unsigned int: "unsigned int", long: "long", \
    double: "double", float: "float", char *: "char *", \
    int *: "int *", default: "other")

#define my_sqrt(x) _Generic((x), float: sqrtf(x), default: sqrt(x))

int main(void) {
    int i = 0; double d = 0; float f = 0; long l = 0; unsigned u = 0;
    int *p = &i;
    printf("%s %s %s %s %s %s %s %s\n", typename(i), typename(d), typename(f),
           typename(l), typename(u), typename(p), typename("str"), typename(i + d));
    printf("%.1f %.1f\n", (double)my_sqrt(9.0f), my_sqrt(16.0));
    int arr[3] = {1, 2, 3};
    printf("decay=%d qual=%d\n", _Generic(arr, int *: 1, default: 0),
           _Generic((const int){5}, int: 1, default: 0));
    return 0;
}
