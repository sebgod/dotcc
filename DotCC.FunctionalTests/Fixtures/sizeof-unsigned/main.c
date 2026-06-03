// `sizeof` yields size_t (an UNSIGNED type) per C / gcc / MSVC — dotcc lowers it
// to ulong. This exercises the unsigned array-cap idiom (a large value / sizeof)
// and the array-length idiom, both of which were ambiguous (CS0034) when dotcc
// modeled sizeof as a signed C# int. Oracled.
#include <stdio.h>
#include <stddef.h>

typedef struct { double a, b; } Big;   // 16 bytes

int main(void) {
    size_t n = sizeof(Big);                  // 16
    size_t cap = (size_t)1600 / sizeof(Big); // 100 (size_t / size_t, unsigned)
    printf("%d %d\n", (int)n, (int)cap);

    int arr[10];
    printf("%d\n", (int)(sizeof(arr) / sizeof(arr[0]))); // 10

    return 0;
}
