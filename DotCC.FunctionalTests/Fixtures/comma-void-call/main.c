// A comma whose LEADING operand is a void CALL: `(voidcall, value)`. The void
// result can't be a C# tuple element (CS8210). In a hoistable spot it lifts to a
// statement; in a non-hoistable spot (deep inside a short-circuiting condition,
// where hoisting would run the side effect unconditionally) it lowers to an
// immediately-invoked delegate that runs the call then yields the value. Lua's
// llex/lapi use this shape pervasively. Oracled.
#include <stdio.h>

static void note(int *log, int v) { *log += v; }  // void side effect

int main(void) {
    int log = 0;
    int *lp = &log;

    // Hoistable: value-context comma in a decl initializer.
    int a = (note(lp, 10), 5);
    printf("a=%d log=%d\n", a, log);

    // Non-hoistable: inside a short-circuiting && (the side effect runs because
    // the left side is true).
    if (a == 5 && (note(lp, 100), 1)) {
        printf("ran log=%d\n", log);
    }

    // Short-circuit must hold: left side false => the void call must NOT run.
    if (a == 999 && (note(lp, 1000), 1)) { printf("unreached\n"); }

    printf("final log=%d\n", log);
    return 0;
}
