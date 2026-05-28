/* <ctype.h> predicates + case conversion. Walks each ASCII range
   through every relevant predicate and accumulates a tally so the
   output is small and deterministic — easier to diff than per-char
   printf. Casts toupper/tolower results to int explicitly for portable
   formatting. */

#include <stdio.h>
#include <ctype.h>

int main(void)
{
    int alpha = 0, digit = 0, alnum = 0, space = 0, upper = 0, lower = 0,
        xdigit = 0, cntrl = 0, print = 0, graph = 0, punct = 0;

    /* Iterate the full ASCII range. */
    for (int c = 0; c < 128; c++) {
        if (isalpha(c))  alpha++;
        if (isdigit(c))  digit++;
        if (isalnum(c))  alnum++;
        if (isspace(c))  space++;
        if (isupper(c))  upper++;
        if (islower(c))  lower++;
        if (isxdigit(c)) xdigit++;
        if (iscntrl(c))  cntrl++;
        if (isprint(c))  print++;
        if (isgraph(c))  graph++;
        if (ispunct(c))  punct++;
    }

    printf("alpha=%d digit=%d alnum=%d\n", alpha, digit, alnum);
    printf("space=%d upper=%d lower=%d\n", space, upper, lower);
    printf("xdigit=%d cntrl=%d print=%d\n", xdigit, cntrl, print);
    printf("graph=%d punct=%d\n", graph, punct);

    /* Case conversion. */
    printf("toupper('a')=%c tolower('Z')=%c\n",
        (char)toupper('a'), (char)tolower('Z'));
    /* Identity on already-cased / non-letter input. */
    printf("toupper('A')=%c tolower('z')=%c toupper('5')=%c\n",
        (char)toupper('A'), (char)tolower('z'), (char)toupper('5'));

    return 0;
}
