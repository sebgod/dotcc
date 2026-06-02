/* A user identifier `L` (Lua's ubiquitous `lua_State *L` parameter) shadows the
 * `using static Libc` string-literal helper `L(...)`. dotcc lowers a C string
 * literal to a call of that helper, so with `L` in scope a bare `L("...")` would
 * try to "call" the variable `L` (CS0149 method-name-expected). dotcc emits the
 * helper fully qualified as `Libc.L(...)` so it always resolves to the helper,
 * regardless of any user `L`. */
#include <stdio.h>

static int show(int L) {                       /* `L` shadows the string helper */
    printf("L=%d msg=%s\n", L, "hi");          /* literals "L=%d msg=%s\n" and "hi" */
    return L;
}

int main(void) {
    int L = 7;                                 /* `L` in scope here too */
    printf("%s %d\n", "start", L);             /* literals "%s %d\n" and "start" */
    return show(L + 35) == 42 ? 0 : 1;
}
