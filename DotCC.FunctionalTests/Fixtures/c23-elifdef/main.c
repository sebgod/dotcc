/* #elifdef / #elifndef (C23 §6.10.1) — sugar for `#elif defined(NAME)` /
 * `#elif !defined(NAME)`. Exercises: an #elifdef arm winning after a false
 * #if, an #elifndef arm winning, and the arm-selection lock (a later
 * #elifdef never fires once the chain emitted). Requires -std=c23 (see
 * std.txt); MSVC opts out. */
#include <stdio.h>

#define HAVE_B 1

#if defined(HAVE_A)
static const char *pick = "A";
#elifdef HAVE_B
static const char *pick = "B";
#else
static const char *pick = "none";
#endif

int main(void) {
    printf("picked %s\n", pick);
#if 0
    printf("dead\n");
#elifndef HAVE_C
    printf("elifndef works\n");
#elifdef HAVE_B
    printf("locked out\n");
#endif
    return 0;
}
