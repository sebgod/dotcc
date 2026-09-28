/* The layout model sizes an enum member as its underlying int. It had modelled
 * one as zero bytes, so a flexible member after an enum was addressed on top
 * of it and an offsetof() array bound came out short, where the C# struct
 * itself was right. */
#include <stdio.h>
#include <stdlib.h>
#include <stddef.h>

enum color { RED, GREEN, BLUE };

struct tagged { char kind; enum color c; char name[]; };
struct pair { char a; enum color c; int x; };

int main(void) {
    printf("%zu %zu %zu %zu\n", sizeof(struct tagged), offsetof(struct tagged, name),
           sizeof(struct pair), offsetof(struct pair, x));

    char pad[offsetof(struct pair, x)];
    printf("%zu\n", sizeof pad);

    struct tagged *t = malloc(sizeof(struct tagged) + 4);
    t->kind = 'k';
    t->c = BLUE;
    t->name[0] = 'a'; t->name[1] = 'b'; t->name[2] = 'c'; t->name[3] = 0;
    printf("%c %d %s\n", t->kind, (int)t->c, t->name);
    free(t);
    return 0;
}
