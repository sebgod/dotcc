/* #pragma pack: N, push/pop (with and without a label), (), and a struct a
   macro expands to. Sizes and offsets match the x86-64 ABI gcc and MSVC share. */
#include <stdio.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

struct Nat { char c; double d; };
#pragma pack(push, 1)
struct P1 { char c; double d; int i; };
typedef struct { uint8_t tag; union { double r; struct { double w, h; } wh; } u; } Shape;
#define S struct
S ViaMacro { char c; int i; };
#pragma pack(push, inner, 2)
struct P2 { char c; double d; };
#pragma pack(pop, inner)
struct P1b { char c; short s; };
#pragma pack(pop)
struct Nat2 { char c; int i; };
#pragma pack(4)
struct P4 { char c; double d; };
#pragma pack()
struct Nat3 { char c; double d; };
union U9 { double d; char b[9]; };
#pragma pack(1)
union UP9 { double d; char b[9]; };
#pragma pack()

int main(void) {
    printf("Nat %d %d\n", (int)sizeof(struct Nat), (int)offsetof(struct Nat, d));
    printf("P1 %d %d %d\n", (int)sizeof(struct P1), (int)offsetof(struct P1, d), (int)offsetof(struct P1, i));
    printf("Shape %d %d\n", (int)sizeof(Shape), (int)offsetof(Shape, u));
    printf("ViaMacro %d\n", (int)sizeof(struct ViaMacro));
    printf("P2 %d %d\n", (int)sizeof(struct P2), (int)offsetof(struct P2, d));
    printf("P1b %d\n", (int)sizeof(struct P1b));
    printf("Nat2 %d\n", (int)sizeof(struct Nat2));
    printf("P4 %d %d\n", (int)sizeof(struct P4), (int)offsetof(struct P4, d));
    printf("Nat3 %d\n", (int)sizeof(struct Nat3));
    printf("U9 %d UP9 %d\n", (int)sizeof(union U9), (int)sizeof(union UP9));
    /* The packed layout is the real one: a field read through bytes. */
    Shape s;
    s.tag = 2;
    s.u.wh.w = 3.5;
    s.u.wh.h = 4.0;
    double w;
    memcpy(&w, (unsigned char *)&s + 1, sizeof w);
    printf("w %g h %g\n", w, s.u.wh.h);
    struct P1 arr[2];
    printf("stride %d\n", (int)((char *)&arr[1] - (char *)&arr[0]));
    return 0;
}
