#include <stdio.h>

/* Backslash-newline line continuation (C translation phase 2). The macro body
   is split across four physical lines; after splicing it is one logical line. */
#define SUM3(a, b, c) \
    ((a) +            \
     (b) +            \
     (c))

/* Continuation inside a string literal — splicing happens before tokenization,
   so the two physical lines join into one string. */
#define GREETING "lua needs \
line continuation"

int main(void) {
    printf("%d\n", SUM3(10, 20, 12));
    printf("%s\n", GREETING);
    return 0;
}
