/* typedef of an ARRAY type (C89) — chibi-scheme's
 * `typedef char sexp_abi_identifier_t[8];`. The alias denotes char[8]:
 * a local declares an array, a struct member is an inline buffer, a
 * parameter decays to char*, sizeof is the array size. Also covers the
 * array declarator in a multi-declarator TAIL (`char *s = ..., buf[4];`,
 * chibi sexp.c) and an enum with a trailing comma (C99). */
#include <stdio.h>
#include <string.h>

typedef char abi_id[8];

struct lib {
    int tag;
    abi_id abi;
};

enum kind {
    KIND_A,
    KIND_B,
    KIND_C,   /* trailing comma is C99-legal */
};

static int check(const abi_id got, const abi_id want) {
    return strncmp(got, want, sizeof(abi_id)) == 0;
}

int main(void) {
    abi_id mine;
    struct lib l;
    char *s = "abc", buf[4];

    strcpy(mine, "chibi");
    l.tag = KIND_C;
    strcpy(l.abi, "chibi");
    strcpy(buf, s);

    printf("%lu\n", (unsigned long)sizeof(abi_id));
    printf("%lu\n", (unsigned long)sizeof(mine));
    printf("%d\n", check(mine, l.abi));
    printf("%d\n", l.tag);
    printf("%s %s\n", mine, buf);
    return 0;
}
