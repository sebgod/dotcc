#include <stdio.h>
#include <string.h>

/* dotcc #249: every argument of a printf-family call is evaluated before anything is written. */

static int noisy(void) {
    printf("[side]");
    return 7;
}

static char word[8] = "old";

static int rewrite(void) {
    strcpy(word, "new");
    return 5;
}

int main(void) {
    printf("a=%d b=%d\n", 1, noisy());

    char buf[32];
    int n = sprintf(buf, "%s %d", word, rewrite());
    printf("%s (%d)\n", buf, n);

    int m = printf("x=%d y=%d\n", 3, 4);
    printf("returned %d\n", m);
    return 0;
}
