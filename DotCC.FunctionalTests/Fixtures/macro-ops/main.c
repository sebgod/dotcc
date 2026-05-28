// # stringify, ## paste, __VA_ARGS__ variadic — the three macro operators
// that depend on having function-like macros. MSVC oracle confirms each
// shape produces identical stdout.

#include "stdio.h"

// Stringification: turn the arg's source text into a string literal.
#define STR(x) #x

// Token-pasting: glue two tokens into one identifier.
// Combined with rescan, the pasted name can refer to another macro.
#define value_1 111
#define value_2 222
#define PICK(n) value_##n

// Both ## and # in the same macro.
#define LABELED_STR(label, x) STR(label = x)

// Variadic macro — classic logging shape.
#define LOG(fmt, ...) printf(fmt, __VA_ARGS__)

// Variadic with a named leading param. Helps simulate a real "level" + msg shape.
#define WARN(level, ...) printf("[L%d] ", level); printf(__VA_ARGS__)

int main() {
    // Stringification.
    printf("STR(hello) = %s\n", STR(hello));
    printf("STR(1 + 2) = %s\n", STR(1 + 2));

    // Token-paste with rescan: PICK(1) → value_1 → 111.
    printf("pick 1 = %d\n", PICK(1));
    printf("pick 2 = %d\n", PICK(2));

    // # and ## combined.
    printf("%s\n", LABELED_STR(age, 30));

    // Variadic: __VA_ARGS__ expands to the trailing args.
    LOG("a=%d b=%d c=%d\n", 1, 2, 3);

    // Variadic with a named param.
    WARN(7, "code=%d msg=%s\n", 42, "ok");

    return 0;
}
