#include <stdio.h>
#include <stddef.h>

/* GH #215: the typedef's declarator is `Alloc`, not a member's name. */
typedef struct {
    void *(*alloc)(void *ctx, size_t size);
    void (*release)(void *ctx, void *ptr);
    void *ctx;
} Alloc;

/* GH #216: typedef names reused as parameters, locals and members. */
typedef int string;
typedef void (*destructor)(int *);
typedef int digit;

struct capsule {
    destructor destructor;
    unsigned char digit;
};

static int count;
static void bump(int *p) { count += *p; }

static int length(string *string, int n) {
    int total = 0;
    for (int i = 0; i < n; i++) { total += string[i]; }
    return total;
}

static int shadow_local(void) {
    string string = 5;
    string += 2;
    return string;
}

static int after_scope(void) {
    string s = 40;
    return s + 2;
}

static void *my_alloc(void *ctx, size_t size) { (void)ctx; (void)size; return NULL; }
static void my_release(void *ctx, void *ptr) { (void)ctx; (void)ptr; }

int main(void) {
    Alloc a = { my_alloc, my_release, NULL };
    int xs[3] = { 1, 2, 3 };
    struct capsule c = { bump, 7 };
    struct capsule *pc = &c;
    int v = 11;
    pc->destructor(&v);
    c.destructor(&v);
    a.release(a.ctx, NULL);
    printf("%d\n", a.alloc(a.ctx, 8) == NULL);
    printf("%d\n", length(xs, 3));
    printf("%d\n", shadow_local());
    printf("%d\n", after_scope());
    printf("%d\n", count);
    printf("%d\n", (int)pc->digit);
    printf("%d\n", (int)offsetof(struct capsule, digit) > 0);
    for (int digit = 0; digit < 2; digit++) { printf("d%d\n", digit); }
    return 0;
}
