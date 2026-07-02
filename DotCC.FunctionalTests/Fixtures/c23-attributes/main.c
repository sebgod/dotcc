/* C23 [[attributes]] — accepted and ignored (no lowering carries them).
 * Exercises: file-scope decl-leading specs (bare / call-arg / namespaced /
 * chained), a block-scope [[maybe_unused]] local, [[fallthrough]] as an
 * attribute-declaration inside a switch, and [[noreturn]] via the c23
 * keyword promotion. Requires -std=c23 (see std.txt); MSVC opts out. */
#include <stdio.h>
#include <stdlib.h>

[[nodiscard]] int answer(void);

[[deprecated("use answer")]] [[maybe_unused]] int old_answer(void) { return 41; }

[[gnu::aligned(16)]] int aligned_global = 5;

[[noreturn]] void die(const char *why) {
    printf("dying: %s\n", why);
    exit(3);
}

int answer(void) { return 42; }

int main(void) {
    [[maybe_unused]] int local = 7;
    int r = answer() + aligned_global;
    switch (r) {
    case 47:
        printf("attributes accepted\n");
        [[fallthrough]];
    case 48:
        printf("fell through\n");
        break;
    default:
        die("unexpected sum");
    }
    return 0;
}
