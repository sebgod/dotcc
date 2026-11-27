/* Struct, union and enum tags spelled like C# reserved keywords are legal C.
 * dotcc @-escapes them wherever it writes a type name, so `object` and
 * `string` bind to the user's struct, not System.Object / System.String. */
#include <stdio.h>

struct object { int x; int y; };
struct string { char c; struct object inner[2]; };
union base { int i; char b[4]; };
enum event { A = 1, B = 4 };
typedef struct { int v; } lock;
struct class { int n; };
struct operator { int op; };
struct params { int p; };
struct ref { int r; };
struct byte { int b; };
struct bool { int t; };
struct decimal { int d; };
struct fixed;

int object = 7;
struct fixed *fp = 0;

struct event_holder { enum event e; };

int main(void)
{
    struct object o = { 1, 2 };
    struct object *po = &o;
    po->y = 5;
    struct string s = { 'x', { { 3, 4 }, { 5, 6 } } };
    union base u;
    u.i = 0x01020304;
    enum event ev = B;
    lock l;
    l.v = 9;
    struct class c = { 10 };
    struct operator op = { 11 };
    struct params pa = { 12 };
    struct ref rf = { 13 };
    struct byte by = { 14 };
    struct bool bo = { 15 };
    struct decimal de = { 16 };
    printf("%d %c\n", o.x, s.c);
    printf("%d %d %d\n", po->y, s.inner[1].x, s.inner[1].y);
    printf("%d %d\n", u.i, ev + A);
    printf("%d %d %d %d %d %d %d %d\n", l.v, c.n, op.op, pa.p, rf.r, by.b, bo.t, de.d);
    printf("%d %d %d\n", (int)sizeof(struct object), (int)sizeof(struct string), (int)sizeof(union base));
    printf("%d %d\n", object, fp == 0);
    return 0;
}
