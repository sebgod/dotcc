// Exercises switch/case/default plus the implicit numeric-condition path.
// Maps 1..7 to short day names; falls through to "unknown" via default.

#include "stdio.h"

void print_day(int d) {
    switch (d) {
        case 1:
            printf("Mon\n");
            break;
        case 2:
            printf("Tue\n");
            break;
        case 3:
            printf("Wed\n");
            break;
        case 4:
            printf("Thu\n");
            break;
        case 5:
            printf("Fri\n");
            break;
        case 6:
            printf("Sat\n");
            break;
        case 7:
            printf("Sun\n");
            break;
        default:
            printf("unknown\n");
            break;
    }
}

int main() {
    for (int i = 0; i <= 8; i++) {
        print_day(i);
    }
    return 0;
}
