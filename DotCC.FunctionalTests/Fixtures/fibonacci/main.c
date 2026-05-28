// Fibonacci via for-loop with compound assignment.
// Tabulates F(0)..F(10) iteratively to avoid blowing the stack.
#include "stdio.h"

int main() {
    int a = 0;
    int b = 1;
    printf("F(0) = %d\n", a);
    for (int i = 1; i <= 10; i++) {
        printf("F(%d) = %d\n", i, b);
        int next = a + b;
        a = b;
        b = next;
    }
    return 0;
}
