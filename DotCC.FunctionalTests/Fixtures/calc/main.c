/* A recursive-descent arithmetic evaluator — the kind of small, real C program
 * that exercises a lot of the language at once: a file-scope cursor, mutual
 * recursion through forward declarations, pointer walking, strtod, the
 * sizeof(a)/sizeof(a[0]) array-length idiom, and — the reason this fixture
 * exists — block-scope variable shadowing. factor() declares `v` inside the
 * `( … )` branch AND a separate `v` at the function-body level; C scopes them
 * apart, but C# would reject the pair as CS0136 unless dotcc alpha-renames the
 * collision. gcc is the oracle. */
#include <stdio.h>
#include <stdlib.h>

static const char *cur;

static double expr(void);   /* forward decl: factor() calls expr() */

static void skip_ws(void) {
    while (*cur == ' ' || *cur == '\t') cur++;
}

static double factor(void) {
    skip_ws();
    if (*cur == '(') {
        cur++;
        double v = expr();
        skip_ws();
        if (*cur == ')') cur++;
        return v;
    }
    if (*cur == '-') {
        cur++;
        return -factor();
    }
    char *end;
    double v = strtod(cur, &end);
    cur = end;
    return v;
}

static double term(void) {
    double v = factor();
    for (;;) {
        skip_ws();
        if (*cur == '*') { cur++; v *= factor(); }
        else if (*cur == '/') { cur++; v /= factor(); }
        else return v;
    }
}

static double expr(void) {
    double v = term();
    for (;;) {
        skip_ws();
        if (*cur == '+') { cur++; v += term(); }
        else if (*cur == '-') { cur++; v -= term(); }
        else return v;
    }
}

static double eval(const char *s) {
    cur = s;
    return expr();
}

int main(void) {
    const char *tests[] = {
        "1 + 2 * 3",
        "(1 + 2) * 3",
        "2 * (3 + 4) - 5",
        "10 / 4",
        "-(3 + 4) * 2",
    };
    int n = sizeof(tests) / sizeof(tests[0]);
    for (int i = 0; i < n; i++) {
        printf("%s = %g\n", tests[i], eval(tests[i]));
    }
    return 0;
}
