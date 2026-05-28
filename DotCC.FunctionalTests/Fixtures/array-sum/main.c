// Exercises array declaration + subscript indexing.
// `int arr[N]` is stack-allocated, filled via subscript, summed via subscript.

#include "stdio.h"

int main() {
    int arr[10];

    // Fill with squares: arr[i] = i * i.
    for (int i = 0; i < 10; i++) {
        arr[i] = i * i;
    }

    // Print each, then the sum.
    int sum = 0;
    for (int i = 0; i < 10; i++) {
        printf("arr[%d] = %d\n", i, arr[i]);
        sum += arr[i];
    }
    printf("sum = %d\n", sum);
    return 0;
}
