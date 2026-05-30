/* C23 enum with an explicit fixed underlying type (`enum Name : Type`). dotcc
 * maps the C underlying type to the C# enum base (`unsigned char` -> `byte`) and
 * emits a real `enum Color : byte`. Values must fit the base (200 fits in a
 * byte). gcc accepts this under -std=c2x; MSVC has no C23 frontend (opted out). */
#include <stdio.h>

enum Color : unsigned char {
    Red = 0,
    Green = 1,
    Blue = 200
};

int main(void) {
    enum Color c = Blue;
    enum Color d = Green;
    printf("%d %d\n", c, d);   /* 200 1 */
    return 0;
}
