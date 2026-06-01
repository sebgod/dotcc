/* Block-scope `static` arrays — static storage duration, function-local
 * visibility, initialised once. A local static array has the SAME lifetime as
 * a file-scope array, so dotcc lowers each to a pinned global field under a
 * function-mangled name (Libc.GlobalArray*), with in-function uses rewritten
 * to that name. Covers the read-only lookup-table idiom that Lua leans on
 * (lobject's luaO_ceillog2 log_2[], lgc's nextage[], ltm's luaT_eventname[]).
 * An array OF POINTERS is stored as a pinned nint[] reinterpreted as T**,
 * because C# forbids pointer types as generic type arguments or array
 * elements. gcc/MSVC agree on the output. */
#include <stdio.h>

/* scalar lookup table — Lua's luaO_ceillog2 shape */
static int ceillog2(unsigned int x) {
    static const unsigned char log_2[8] = {0, 1, 2, 2, 3, 3, 3, 3};
    return log_2[x];
}

/* string table — an array OF POINTERS (Lua's luaT_eventname) */
static const char *eventname(int i) {
    static const char *const names[] = {"add", "sub", "mul"};
    return names[i];
}

int main(void) {
    /* the static persists across calls and re-reads the same table */
    printf("%d %d %d %d\n", ceillog2(1), ceillog2(4), ceillog2(7), ceillog2(2));
    printf("%s %s %s\n", eventname(0), eventname(1), eventname(2));
    return 0;
}
