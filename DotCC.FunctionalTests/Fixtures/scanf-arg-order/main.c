#include <stdio.h>
#include <string.h>

/* dotcc #249: every argument of a scanf-family call is evaluated before any input is read or target stored. */

static char word[16] = "1";
static int v;

static int *rewrite(void) {
    strcpy(word, "42");
    return &v;
}

int main(void) {
    int arr[4] = {0, 0, 0, 0};
    int i = 0;
    int n = sscanf("3 9", "%d %d", &i, &arr[i]);
    printf("n=%d i=%d arr0=%d arr3=%d\n", n, i, arr[0], arr[3]);

    int m = sscanf(word, "%d", rewrite());
    printf("m=%d v=%d\n", m, v);
    return 0;
}
