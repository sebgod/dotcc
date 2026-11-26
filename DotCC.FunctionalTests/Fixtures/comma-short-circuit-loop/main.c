/* A comma whose leading operand is a void call, in the right operand of && and
   || and in loop conditions. Where a statement can take temps, && and || become
   an if over a bool temp, and a loop whose condition hoists evaluates it at the
   top of each iteration (a do-while after a label, which `continue` reaches):
   no closure, which could not capture `local`, whose address is taken. */
#include <stdio.h>

static int calls;
static void note(int v) { calls += v; }

int main(void) {
    int i = 0, sum = 0, local = 3;
    int *p = &local;
    int a = (i == 0) && (note(1), local > 2);
    int b = (i != 0) && (note(100), 1);
    int c = (i != 0) || (note(10), *p == 3);
    if ((i == 0) && (note(2), *p > 1)) sum += 1000;
    while (i < 5 && (note(1000), local > 0)) {
        i++;
        if (i == 2) continue;
        sum += i;
    }
    for (int j = 0; j < (j > 3 ? (note(5), 0) : 10); j++) {
        if (j == 1) continue;
        sum += j * 10;
    }
    int k = 0;
    do {
        k++;
        if (k == 2) continue;
        sum += 100;
    } while (k < 4 && (note(10000), *p));
    printf("%d %d %d %d %d %d %d\n", a, b, c, i, k, sum, calls);
    return 0;
}
