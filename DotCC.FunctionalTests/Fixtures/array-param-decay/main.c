// Exercises C array-parameter decay: both forms of the unsized `T arr[]`
// and the sized `T arr[N]` parameter declarations are semantically pointer
// parameters at the function-type boundary (C99 §6.7.5.3p7). The same
// underlying array can be passed to any of the three spellings.

#include "stdio.h"

// Plain pointer form — the baseline.
int sum_ptr(int* arr, int n) {
    int s = 0;
    for (int i = 0; i < n; i++) {
        s += arr[i];
    }
    return s;
}

// Unsized array form — semantically identical to `int*`.
int sum_unsized(int arr[], int n) {
    int s = 0;
    for (int i = 0; i < n; i++) {
        s += arr[i];
    }
    return s;
}

// Sized array form — the `5` is documentation only; C compilers don't
// even warn at the call site if you pass a different-sized array.
int sum_sized(int arr[5], int n) {
    int s = 0;
    for (int i = 0; i < n; i++) {
        s += arr[i];
    }
    return s;
}

int main() {
    int arr[5];
    for (int i = 0; i < 5; i++) {
        arr[i] = (i + 1) * 10;
    }

    // All three call sites pass the same `int*` (array-to-pointer decay).
    printf("ptr=%d\n", sum_ptr(arr, 5));
    printf("unsized=%d\n", sum_unsized(arr, 5));
    printf("sized=%d\n", sum_sized(arr, 5));
    return 0;
}
