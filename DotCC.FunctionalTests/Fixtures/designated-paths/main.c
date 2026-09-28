/* Designations as paths (GH #221, C11 6.7.9p17-20): a member of an anonymous
   union (CPython's `_PyObject_HEAD_INIT` sets `.ob_refcnt`), nested member
   designators (`.op.code =`, `.v.Name.id =`), an array designator with a
   braced element (`[BEFORE_ASYNC_WITH] = { ... }`), `[i].x`, positional
   continuation after a designated subobject, brace elision in struct arrays,
   and a short struct array zero-filling its tail. */
#include <stdio.h>

struct pyobj {
    union { long ob_refcnt; unsigned int ob_refcnt_split[2]; };
    const char *ob_type;
};

struct instr { struct { int code, arg; } op; int line; };

struct name { int id; int ctx; };
struct expr { int kind; union { struct name Name; long Constant; } v; };

enum opcode { NOP, LOAD, BEFORE_ASYNC_WITH, NUM_OPS };
struct meta { int valid; int fmt; int flags; };
static const struct meta metadata[NUM_OPS] = {
    [BEFORE_ASYNC_WITH] = { 1, 3, 0x10 },
    [LOAD] = { 1, 2 },
};

struct point { int x, y; };
struct inner { int b, c; };
struct outer { struct inner a; int d; };
union num { int i; double f; };

int main(void) {
    struct pyobj o = { .ob_refcnt = 5, .ob_type = "type" };
    printf("%ld %s\n", o.ob_refcnt, o.ob_type);

    struct instr ins = { .op.code = 7, .op.arg = 9, .line = 12 };
    printf("%d %d %d\n", ins.op.code, ins.op.arg, ins.line);

    struct expr e = { .kind = 1, .v.Name.id = 42, .v.Name.ctx = 3 };
    printf("%d %d %d\n", e.kind, e.v.Name.id, e.v.Name.ctx);

    for (int i = 0; i < NUM_OPS; i++) {
        printf("[%d] %d %d %d\n", i, metadata[i].valid, metadata[i].fmt, metadata[i].flags);
    }

    struct point pts[4] = { [1].x = 5, [1].y = 6, [3] = { 7, 8 } };
    printf("%d %d %d %d %d\n", pts[0].x, pts[1].x, pts[1].y, pts[3].x, pts[3].y);

    struct outer ou = { .a.b = 1, 2, 3 };
    printf("%d %d %d\n", ou.a.b, ou.a.c, ou.d);

    struct point elided[2] = { 1, 2, 3, 4 };
    struct point shorter[3] = { { 9, 9 } };
    printf("%d %d %d %d | %d %d %d\n", elided[0].x, elided[0].y, elided[1].x, elided[1].y,
           shorter[0].x, shorter[2].x, shorter[2].y);

    int grid[2][3] = { [1][2] = 9, [0][1] = 4 };
    printf("%d %d %d %d\n", grid[0][0], grid[0][1], grid[1][1], grid[1][2]);

    union num n = { .f = 1.5 };
    printf("%.1f\n", n.f);
    return 0;
}
