#include <stdio.h>

/* C23 empty initializer `{}` — zero-initialize any object. dotcc lowers a
   scalar/struct to `= default`, a sized array to a zeroed stackalloc, and an
   empty compound literal `(T){}` to `default(T)`. */

struct Point { int x; int y; };

int main(void) {
    int n = {};
    struct Point p = {};
    int a[4] = {};
    struct Point q = (struct Point){};

    int sum = a[0] + a[1] + a[2] + a[3];
    printf("%d %d %d %d %d %d\n", n, p.x, p.y, sum, q.x, q.y);
    return 0;
}
