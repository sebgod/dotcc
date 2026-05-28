// Factorial via a for-loop with postfix ++. Prints 0! through 6!.
#include "stdio.h"

int factorial(int n) {
    int result = 1;
    for (int i = 2; i <= n; i++) {
        result *= i;
    }
    return result;
}

int main() {
    for (int n = 0; n <= 6; n++) {
        printf("%d! = %d\n", n, factorial(n));
    }
    return 0;
}
