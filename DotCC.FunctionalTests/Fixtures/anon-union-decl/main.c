/* An ANONYMOUS union type used directly in a declaration (not a typedef body or
 * a union member) — Lua lstrlib's native-endianness probe:
 *   static const union { int dummy; char little; } nativeendian = {1};
 * dotcc synthesizes a name for the unnamed union, emits it as an explicit-layout
 * (overlapping) struct, and lets the ordinary declaration / aggregate-init
 * machinery handle the rest (the `{…}` init targets the FIRST union member).
 * Both file-scope-static-with-init and block-scope-local forms are exercised.
 * Values chosen to avoid endianness dependence: each test reads back the SAME
 * member it wrote (a reinterpret across members would be endianness-defined). */
#include <stdio.h>

/* file-scope: anonymous union type + brace aggregate init (first member). */
static const union { int dummy; char bytes[4]; } u = {0x04030201};

int main(void) {
    /* block-scope: anonymous union type as a local variable. */
    union { int i; unsigned x; } v;
    v.i = -1;
    printf("u.dummy=%d\n", u.dummy);    /* 67305985 — read the member we set */
    printf("reinterpret=%u\n", v.x);    /* 4294967295 — two's complement (C23 §6.2.6) */
    return 0;
}
