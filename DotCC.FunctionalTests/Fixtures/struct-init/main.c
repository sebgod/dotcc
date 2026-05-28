// Positional struct aggregate init: `Point p = {1, 2};`. Three shapes:
//   - bare struct decl
//   - typedef'd struct used as alias
//   - partial init (trailing fields take their zero default)

#include "stdio.h"

struct Point {
    int x;
    int y;
};

typedef struct Vec3 {
    int x;
    int y;
    int z;
} Vec3;

int main() {
    // Bare struct + positional init.
    struct Point p = {3, 4};
    printf("p: (%d, %d)\n", p.x, p.y);

    // typedef'd struct.
    Vec3 v = {10, 20, 30};
    printf("v: (%d, %d, %d)\n", v.x, v.y, v.z);

    // Partial init — only x supplied; y and z default to 0.
    Vec3 partial = {7};
    printf("partial: (%d, %d, %d)\n", partial.x, partial.y, partial.z);

    return 0;
}
