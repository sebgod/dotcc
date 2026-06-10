/* offsetof with a dotted member-designator (C99 7.17) — chibi-scheme's
 * `#define sexp_offsetof(type, f) (offsetof(struct sexp_struct, value.type.f))`.
 * Walks nested structs and unions; a union level contributes 0. */
#include <stdio.h>
#include <stddef.h>

struct flonum { double f; };
struct pair { void *car, *cdr; };

union value {
    struct flonum flonum;
    struct pair pair;
};

struct sexp {
    int tag;
    union value value;
};

int main(void) {
    printf("%lu\n", (unsigned long)offsetof(struct sexp, tag));
    printf("%lu\n", (unsigned long)offsetof(struct sexp, value));
    printf("%lu\n", (unsigned long)offsetof(struct sexp, value.pair.car));
    printf("%lu\n", (unsigned long)offsetof(struct sexp, value.pair.cdr));
    printf("%lu\n", (unsigned long)offsetof(struct sexp, value.flonum.f));
    return 0;
}
