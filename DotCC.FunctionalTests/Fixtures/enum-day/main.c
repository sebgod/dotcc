// Enum: auto-numbered + explicit values, mixed. Used in switch, as int,
// and as parameter type `enum Color`.

#include "stdio.h"

enum Color {
    Red,           // 0
    Green,         // 1
    Blue = 5,      // 5
    Yellow,        // 6 (Blue + 1)
    Purple         // 7
};

enum Status {
    OK = 200,
    NotFound = 404,
    ServerError = 500
};

int color_value(enum Color c) {
    // Enumerators usable as plain ints — no casts needed because
    // we lower to `const int`.
    return c * 10;
}

int main() {
    printf("Red=%d Green=%d Blue=%d Yellow=%d Purple=%d\n",
        Red, Green, Blue, Yellow, Purple);
    printf("OK=%d NotFound=%d ServerError=%d\n",
        OK, NotFound, ServerError);

    // Use enum in declarations and switches.
    enum Color c = Yellow;
    switch (c) {
        case Red:
            printf("red\n");
            break;
        case Yellow:
            printf("yellow\n");
            break;
        default:
            printf("other\n");
            break;
    }

    printf("color_value(Blue)=%d\n", color_value(Blue));

    return 0;
}
