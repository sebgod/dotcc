// Exercises logical `!`, ternary `?:`, do-while, and char literals.

#include "stdio.h"

int main() {
    // Logical NOT: !0 == 1, !1 == 0, !!x == 1 for truthy x.
    int a = !0;
    int b = !1;
    int c = !!42;
    printf("!0=%d !1=%d !!42=%d\n", a, b, c);

    // Ternary as expression.
    int x = 10;
    int y = 20;
    int max = x > y ? x : y;
    int min = x < y ? x : y;
    printf("max=%d min=%d\n", max, min);

    // Nested / chained ternary (right-associative): grade A / B / C / F.
    int score = 75;
    char grade = score >= 90 ? 'A' : score >= 80 ? 'B' : score >= 70 ? 'C' : 'F';
    printf("score=%d grade=%c\n", score, grade);

    // do-while runs body at least once even when condition is initially false.
    int n = 0;
    int count = 0;
    do {
        count = count + 1;
        n = n + 1;
    } while (n < 0);
    printf("do-once count=%d\n", count);

    // do-while as a real loop: digit-print loop on 6.
    int v = 6;
    do {
        printf("%d ", v);
        v = v - 1;
    } while (v > 0);
    printf("\n");

    // char literal comparisons and escapes.
    char ch = 'a';
    if (ch == 'a') {
        printf("matched 'a'\n");
    }
    char nl = '\n';
    char tab = '\t';
    printf("nl=%d tab=%d backslash=%d\n", nl, tab, '\\');

    return 0;
}
