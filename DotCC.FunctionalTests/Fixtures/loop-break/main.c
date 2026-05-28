// Exercises `break` and `continue` inside a while loop.
//
// Walks i from 1..10; skips evens via `continue`, stops at 7 via `break`.
// Should print:
//   1
//   3
//   5
// (Then "done" once outside the loop.)

#include "stdio.h"

int main() {
    int i = 0;
    while (i < 10) {
        i = i + 1;
        // Evens: skip the rest of the body, head back to the loop test.
        if (i - ((i / 2) * 2) == 0) {
            continue;
        }
        // First odd >= 7: stop the loop entirely.
        if (i > 6) {
            break;
        }
        printf("%d\n", i);
    }
    printf("done\n");
    return 0;
}
