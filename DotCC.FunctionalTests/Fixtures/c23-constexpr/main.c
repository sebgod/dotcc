/* C23 constexpr — compile-time constant object declarations. The folded value
 * (Symbol.ConstValue) resolves in every integer-constant-expression position:
 * array bounds, _Static_assert, case labels, bit-field widths, and _Generic.
 * A constexpr may reference an earlier one. Verified vs gcc -std=c2x. */
#include <stdio.h>

constexpr int ROWS = 3;
constexpr int COLS = 4;
constexpr int CELLS = ROWS * COLS;

struct BitCells { unsigned flags : (CELLS / 3); };

int main(void) {
    constexpr int scale = 10;
    int table[CELLS];
    _Static_assert(CELLS == 12, "CELLS");
    _Static_assert(scale * scale == 100, "scale");

    for (int i = 0; i < CELLS; i++) table[i] = i * scale;

    int sum = 0;
    for (int i = 0; i < CELLS; i++) sum += table[i];

    switch (scale) {
        case 10: printf("case=ten "); break;
        default: printf("case=other "); break;
    }

    printf("rows=%d cols=%d cells=%d sizeof=%d bitfield=%d sum=%d sel=%s\n",
           ROWS, COLS, CELLS, (int)sizeof(table),
           (int)sizeof(struct BitCells), sum,
           _Generic(CELLS, int: "int", long: "long", default: "other"));
    return 0;
}
