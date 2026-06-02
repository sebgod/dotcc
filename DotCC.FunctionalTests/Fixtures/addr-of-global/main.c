// Taking the address of a file-scope global / static local. In C these have
// stable addresses; dotcc lowers them to C# static fields, which are MOVEABLE
// variables (so a bare &field is CS0212). The address is handed back via
// Unsafe.AsPointer (dotcc's globals are unmanaged value types in non-moving
// static storage, so it's stable). Lua leans on this (&absentkey, &dummynode_).
#include <stdio.h>

typedef struct { int tag; int payload; } Cell;
static const Cell sentinel = { 7, 42 };
static int globalCount = 0;

const Cell *get_sentinel(void) { return &sentinel; }   // &global struct, returned

int next_id(void) {
    static int seed = 1000;   // static local
    int *p = &seed;           // &static-local
    *p += 1;
    return seed;
}

int main(void) {
    const Cell *s = get_sentinel();
    int *g = &globalCount;     // &global scalar
    *g = 5;
    printf("%d %d %d\n", s->tag, s->payload, globalCount);
    printf("%d %d %d\n", next_id(), next_id(), next_id());
    return 0;
}
