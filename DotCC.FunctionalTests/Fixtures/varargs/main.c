#include <stdio.h>
#include <stdarg.h>

/* classic variadic: sum `count` ints pulled via va_arg */
static int sum(int count, ...) {
    va_list ap;
    va_start(ap, count);
    int total = 0;
    for (int i = 0; i < count; i++) {
        total += va_arg(ap, int);
    }
    va_end(ap);
    return total;
}

/* takes a va_list (the v-form), reads mixed types incl. a pointer */
static void vshow(const char *prefix, va_list ap) {
    int n = va_arg(ap, int);
    const char *s = va_arg(ap, char *);
    double d = va_arg(ap, double);
    printf("%s n=%d s=%s d=%.1f\n", prefix, n, s, d);
}

/* variadic forwarder: va_start then hand the va_list to the v-form */
static void show(const char *prefix, ...) {
    va_list ap;
    va_start(ap, prefix);
    vshow(prefix, ap);
    va_end(ap);
}

int main(void) {
    printf("sum=%d\n", sum(4, 10, 20, 30, 40));
    show("LOG", 7, "hi", 3.5);
    return 0;
}
