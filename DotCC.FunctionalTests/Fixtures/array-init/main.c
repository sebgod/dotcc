// Aggregate initializers for arrays. Two forms: sized (`int arr[5] = {…}`)
// and derived-size (`int arr[] = {…}` — count from initializer).

#include "stdio.h"

int main() {
    // Sized init.
    int sized[5] = {10, 20, 30, 40, 50};
    int total = 0;
    for (int i = 0; i < 5; i++) {
        total = total + sized[i];
    }
    printf("sized total=%d\n", total);

    // Implicit-size init: count is 4.
    int derived[] = {1, 2, 3, 4};
    int n = 0;
    for (int i = 0; i < 4; i++) {
        n = n + derived[i];
    }
    printf("derived total=%d\n", n);

    // Bytes via char[] init.
    char letters[] = {'h', 'i', '!'};
    printf("letters: %c %c %c\n", letters[0], letters[1], letters[2]);

    return 0;
}
