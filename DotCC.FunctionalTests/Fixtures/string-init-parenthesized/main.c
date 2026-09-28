/* A parenthesized string literal initializes a character array as the bare literal
   does: gcc accepts it (warning only under -pedantic), and CPython's
   _PyASCIIObject_INIT spells `._data = (LITERAL)`. */
#include <stdio.h>

struct S { int k; char d[6]; };

static struct S s = { .k = 1, .d = ("hello") };
static char t[] = ("abc");

int main(void) {
    char u[4] = ("xyz");
    printf("%s %s %s %d %d\n", s.d, t, u, (int)sizeof t, s.k);
    return 0;
}
