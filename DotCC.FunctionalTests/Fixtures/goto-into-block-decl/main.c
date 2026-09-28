/* A goto into a block past its declaration (C11 6.8.6.1 allows it for a variable that
   is not variably modified): the variable exists, its initializer is skipped. The tail
   after the label reads it, as unicodeobject.c's `{ Py_UCS4 ch; restart: ch = ...; }`
   loop does, and a case body in a switch declares one the same way. */
#include <stdio.h>

static int decode(const char *s) {
    int total = 0;
    if (*s == '+') { s++; goto restart; }
    while (*s) {
        {
            int ch = 0;
        restart:
            ch = *s++;
            total += ch - '0';
        }
    }
    return total;
}

static int classify(int op, int arg) {
    int out = 0;
    if (op < 0) { goto shared; }
    switch (op) {
    case 1: {
        int scaled = arg * 10;
    shared:
        scaled = arg * 100;
        out = scaled + 1;
        break;
    }
    case 2:
        out = arg;
        break;
    default:
        out = -1;
        break;
    }
    return out;
}

int main(void) {
    int a = decode("+123");
    int b = decode("45");
    printf("%d %d\n", a, b);
    int c = classify(1, 2);
    int d = classify(-1, 3);
    int e = classify(2, 7);
    printf("%d %d %d\n", c, d, e);
    return 0;
}
