#include <stdio.h>

/* GH #230: the null pointer constant `0` through function pointers and in
   pointer comparisons. */
typedef void (*cb)(int *);
struct s { cb fn; };

static void report(int *p) { printf("%s\n", p == 0 ? "null" : "set"); }
static void via_param(void (*f)(int *)) { f(0); }

static int is_null(int *p) { return p == 0; }
static int is_set(int *p) { return 0 != p; }
static int no_callback(int (*g)(void)) { return g == 0; }

int main(void) {
    struct s v = { report };
    struct s *pv = &v;
    cb local = report;
    cb table[1] = { report };
    int x = 7;
    v.fn(0);
    pv->fn(0);
    table[0](0);
    (*local)(0);
    local(&x);
    via_param(report);
    printf("%d %d %d %d\n", is_null(0), is_null(&x), is_set(&x), no_callback(0));
    return 0;
}
