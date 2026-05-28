// dotcc demo program. Exercises every major feature of the language:
// - System headers via #include "stdio.h" / "stdlib.h"
// - User headers via #include "math.h" (with #ifndef header guard)
// - Forward declarations resolved from headers
// - printf with format strings (%d, %f, %s)
// - String literals (char*)
// - float + double arithmetic
// - malloc / free via stdlib.h
// - main(int argc, char** argv) — args walked via pointer arithmetic
//
// Also valid C99 — `clang -std=c99 main.c math.c -o demo` produces an
// equivalent native binary.

#include "stdio.h"
#include "stdlib.h"
#include "math.h"

#define N 5

int main(int argc, char** argv) {
    printf("hello, dotcc!\n");

    // Floats and doubles via the math module. `3.5f` is a float literal
    // (suffix-typed); `1.1` and `2.2` are doubles. Real C accepts both
    // suffixes; C# requires the `f` for float literals — same source works
    // in both pipelines because we preserve the suffix in codegen.
    printf("square(3.5)   = %f\n", square(3.5f));
    printf("dsum(1.1,2.2) = %f\n", dsum(1.1, 2.2));

    // Heap-allocated int array, filled and summed via pointer arithmetic.
    int* arr = (int*)malloc(N * 4);
    int i = 0;
    while (i < N) {
        *(arr + i) = i + 1;
        i = i + 1;
    }
    int total = sum_ints(arr, N);
    printf("sum 1..%d = %d\n", N, total);
    free(arr);

    // Walk argv via pointer arithmetic — no array indexing in the initial
    // grammar yet. Real C convention: argv[0] is the program path,
    // argv[1..argc-1] are the user-supplied args. dotcc's emitted shell
    // synthesises argv[0] from the assembly path so this matches what
    // clang would pass.
    if (argc > 1) {
        printf("first arg: %s\n", *(argv + 1));
    }

    return 0;
}
