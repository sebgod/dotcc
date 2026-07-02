/* C23 __VA_OPT__ — variadic-macro comma elision, the standardized
 * replacement for GNU's `, ##__VA_ARGS__`. Exercises: the canonical
 * printf-forwarding LOG shape with and without variadic args, and a
 * group that itself contains __VA_ARGS__ (substitution INSIDE the
 * group). Requires -std=c23 (std.txt); MSVC opts out. */
#include <stdio.h>

#define LOG(fmt, ...) printf(fmt __VA_OPT__(,) __VA_ARGS__)
#define JOIN(first, ...) first __VA_OPT__(+ sum(__VA_ARGS__))

static int sum(int a, int b) { return a + b; }

int main(void) {
    LOG("no args\n");
    LOG("with: %d %s\n", 7, "seven");
    int a = JOIN(1);
    int b = JOIN(1, 20, 21);
    printf("%d %d\n", a, b);
    return 0;
}
