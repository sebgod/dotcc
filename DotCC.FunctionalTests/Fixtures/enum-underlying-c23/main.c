/* C23 enum with an explicit fixed underlying type (`enum Name : Type`). dotcc
 * maps the C underlying type to the C# enum base (`unsigned char` -> `byte`) and
 * emits a real `enum Color : byte`. Values must fit the base (200 fits in a
 * byte). gcc accepts this under -std=c2x; MSVC has no C23 frontend (opted out).
 * The tag is optional on its own (`enum : T { ... }`): an untagged enum's
 * constants have the fixed type, and a typedef'd one is a real enum too. */
#include <stdio.h>

enum Color : unsigned char {
    Red = 0,
    Green = 1,
    Blue = 200
};

enum : long long { Big = 1LL << 40 };
typedef enum : unsigned char { Low, High = 200 } Small;

int main(void) {
    enum Color c = Blue;
    enum Color d = Green;
    printf("%d %d\n", c, d);   /* 200 1 */
    Small s = High;
    printf("%d %d\n", (int)sizeof(Big), (int)sizeof(Small));   /* 8 1 */
    printf("%lld %d\n", Big >> 20, (int)s);   /* 1048576 200 */
    return 0;
}
